using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Web.Mvc;
using AntiPlagiarism.Api;
using AntiPlagiarism.Api.Models.Parameters;
using AntiPlagiarism.Api.Models.Results;
using Database;
using Database.DataContexts;
using Database.Models;
using Microsoft.AspNet.Identity;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using uLearn.Web.FilterAttributes;
using Ulearn.Common;
using Ulearn.Common.Extensions;
using Ulearn.Core;
using Ulearn.Core.Configuration;
using Ulearn.Core.Courses;
using Ulearn.Core.Courses.Slides;
using Ulearn.Core.Courses.Slides.Exercises;

namespace uLearn.Web.Controllers
{
	[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
	public class AntiPlagiarismController : JsonDataContractController
	{
		private readonly UserSolutionsRepo userSolutionsRepo;
		private readonly GroupsRepo groupsRepo;
		private readonly CourseManager courseManager;
		private readonly UserRolesRepo userRolesRepo;
		private static readonly IAntiPlagiarismClient antiPlagiarismClient;

		static AntiPlagiarismController()
		{
			var antiplagiarismClientConfiguration = ApplicationConfiguration.Read<UlearnConfiguration>().AntiplagiarismClient;
			antiPlagiarismClient = new AntiPlagiarismClient(antiplagiarismClientConfiguration.Endpoint, antiplagiarismClientConfiguration.Token);
		}

		public AntiPlagiarismController(UserSolutionsRepo userSolutionsRepo, GroupsRepo groupsRepo, UserRolesRepo userRolesRepo)
		{
			this.userSolutionsRepo = userSolutionsRepo;
			this.groupsRepo = groupsRepo;
			this.userRolesRepo = userRolesRepo;
		}

		public AntiPlagiarismController(ULearnDb db, WebCourseManager courseManager)
			: this(new UserSolutionsRepo(db, courseManager), new GroupsRepo(db, courseManager), new UserRolesRepo(db))
		{
			this.courseManager = courseManager;
		}

		public AntiPlagiarismController()
			: this(new ULearnDb(), WebCourseManager.Instance)
		{
		}

		public async Task<ActionResult> Info(string courseId, int submissionId)
		{
			var submission = userSolutionsRepo.FindSubmissionById(submissionId);
			if (!string.Equals(submission.CourseId, courseId, StringComparison.InvariantCultureIgnoreCase))
				return HttpNotFound();

			var slide = courseManager.FindCourse(courseId)?.FindSlideById(submission.SlideId, true) as ExerciseSlide;
			if (slide == null)
				return HttpNotFound();

			if (!slide.Exercise.CheckForPlagiarism)
				return Json(new AntiPlagiarismInfoModel
				{
					Status = "not_checked",
				}, JsonRequestBehavior.AllowGet);

			var antiPlagiarismsResult = await GetAuthorPlagiarismsAsync(submission).ConfigureAwait(false);

			var model = new AntiPlagiarismInfoModel
			{
				Status = "checked",
				SuspicionLevel = SuspicionLevel.None,
				SuspiciousAuthorsCount = 0,
			};
			var faintSuspicionAuthorsIds = new HashSet<Guid>();
			var strongSuspicionAuthorsIds = new HashSet<Guid>();
			foreach (var researchedSubmission in antiPlagiarismsResult.ResearchedSubmissions)
			{
				foreach (var plagiarism in researchedSubmission.Plagiarisms)
				{
					if (plagiarism.Weight >= antiPlagiarismsResult.SuspicionLevels.StrongSuspicion)
					{
						strongSuspicionAuthorsIds.Add(plagiarism.SubmissionInfo.AuthorId);
						model.SuspicionLevel = SuspicionLevel.Strong;
					}
					else if (plagiarism.Weight >= antiPlagiarismsResult.SuspicionLevels.FaintSuspicion && model.SuspicionLevel != SuspicionLevel.Strong)
					{
						faintSuspicionAuthorsIds.Add(plagiarism.SubmissionInfo.AuthorId);
						model.SuspicionLevel = SuspicionLevel.Faint;
					}
				}
			}

			model.SuspiciousAuthorsCount = model.SuspicionLevel == SuspicionLevel.Faint ? faintSuspicionAuthorsIds.Count : strongSuspicionAuthorsIds.Count;

			return Json(model, JsonRequestBehavior.AllowGet);
		}

		public async Task<ActionResult> Details(string courseId, int submissionId)
		{
			var submission = userSolutionsRepo.FindSubmissionById(submissionId);
			if (!submission.CourseId.EqualsIgnoreCase(courseId))
				return HttpNotFound();

			var antiPlagiarismsResult = await GetAuthorPlagiarismsAsync(submission);
			var mostSimilarSubmissionsHistogramData = await GetMostSimilarSubmissionsHistogramData(submission.SlideId, 100);
			//var mostSimilarSubmissionsHistogramData = GetMostSimilarSubmissionsHistogramDataMock(100);
			var suspicionLevelsResponse = await antiPlagiarismClient.GetSuspicionLevelsAsync(new GetSuspicionLevelsParameters { TaskId = submission.SlideId });

			var antiPlagiarismSubmissionInfos = antiPlagiarismsResult.ResearchedSubmissions.Select(s => s.SubmissionInfo);
			var plagiarismsSubmissionInfos = antiPlagiarismsResult.ResearchedSubmissions
				.SelectMany(s => s.Plagiarisms).Select(p => p.SubmissionInfo);

			var submissionsIds = antiPlagiarismSubmissionInfos.Concat(plagiarismsSubmissionInfos)
				.Select(si => si.SubmissionId)
				.Distinct()
				.ToList();

			var submissions = userSolutionsRepo.FindSubmissionsByIds(submissionsIds)
				.ToDictionary(s => s.Id);
			submissions[submissionId] = submission;

			var userIds = new HashSet<string>(antiPlagiarismsResult.ResearchedSubmissions.SelectMany(s => s.Plagiarisms).Select(s => s.SubmissionInfo.AuthorId.ToString()));
			userIds.Add(submission.UserId);
			/* Use special MockUserCanSeeAllGroups() instead of User because we want to show all users groups, not only available */
			var usersGroups = groupsRepo.GetUsersGroupsNamesAsStrings(courseId, userIds, new MockUserCanSeeAllGroups(), actual: true, archived: false).ToDefaultDictionary();
			var usersArchivedGroups = groupsRepo.GetUsersGroupsNamesAsStrings(courseId, userIds, new MockUserCanSeeAllGroups(), actual: false, archived: true).ToDefaultDictionary();
			var isCourseOrSysAdmin = userRolesRepo.HasUserAccessToCourse(User.Identity.GetUserId(), courseId, CourseRole.CourseAdmin);

			var course = courseManager.FindCourse(courseId);
			var slide = course?.FindSlideById(submission.SlideId, true);
			var details = new AntiPlagiarismDetailsModel
			{
				Course = course,
				Slide = slide,
				SubmissionId = submissionId,
				Submissions = submissions,
				UsersGroups = usersGroups,
				UsersArchivedGroups = usersArchivedGroups,
				AntiPlagiarismResponse = antiPlagiarismsResult,
				MostSimilarSubmissionsHistogramData = mostSimilarSubmissionsHistogramData,
				CanEditSuspicionLevels = isCourseOrSysAdmin,
				SuspicionLevels = suspicionLevelsResponse.SuspicionLevels,
				MaxAuthorSubmissionWeight = antiPlagiarismsResult.ResearchedSubmissions.Select(s => s.Plagiarisms.Select(p => p.Weight).DefaultIfEmpty(0).Max()).Max()
			};
			return View(details);
		}

		public ActionResult SubmissionsPanel(int submissionId, Dictionary<int, double> submissionWeights)
		{
			var submission = userSolutionsRepo.FindSubmissionById(submissionId);
			if (submission == null)
				return HttpNotFound();

			var courseId = submission.CourseId;
			var slideId = submission.SlideId;
			var userId = submission.UserId;
			var slide = courseManager.FindCourse(courseId)?.FindSlideById(slideId, true);
			if (slide == null)
				return HttpNotFound();
			var submissions = userSolutionsRepo.GetAllAcceptedSubmissionsByUser(courseId, slideId, userId).ToList();

			return PartialView("~/Views/Exercise/SubmissionsPanel.cshtml", new ExerciseSubmissionsPanelModel(courseId, slide)
			{
				Submissions = submissions,
				CurrentSubmissionId = submissionId,
				CanTryAgain = false,
				GetSubmissionDescription = s => GetSubmissionDescriptionForPanel(submissionWeights.ContainsKey(s.Id) ? (double?)submissionWeights[s.Id] : null),
				FormUrl = Url.Action("Details", new { courseId }),
				ShowButtons = false,
				SelectControlName = "submissionId",
			});
		}

		private static string GetSubmissionDescriptionForPanel(double? submissionWeight)
		{
			if (!submissionWeight.HasValue)
				return ", не проверялась на списывание";
			var weightPercents = (int)(submissionWeight.Value * 100);
			return $", подозрительность — {weightPercents}%";
		}

		private static readonly ConcurrentDictionary<Tuple<Guid, Guid>, Tuple<DateTime, GetAuthorPlagiarismsResponse>> plagiarismsCache = new ConcurrentDictionary<Tuple<Guid, Guid>, Tuple<DateTime, GetAuthorPlagiarismsResponse>>();
		private static readonly TimeSpan cacheLifeTime = TimeSpan.FromMinutes(10);

		private static async Task<GetAuthorPlagiarismsResponse> GetAuthorPlagiarismsAsync(UserExerciseSubmission submission)
		{
			RemoveOldValuesFromCache();
			var userId = Guid.Parse(submission.UserId);
			var taskId = submission.SlideId;
			var cacheKey = Tuple.Create(userId, taskId);
			if (plagiarismsCache.TryGetValue(cacheKey, out var cachedValue))
			{
				return cachedValue.Item2;
			}

			var value = await antiPlagiarismClient.GetAuthorPlagiarismsAsync(new GetAuthorPlagiarismsParameters
			{
				AuthorId = userId,
				TaskId = taskId
			}).ConfigureAwait(false);
			plagiarismsCache.AddOrUpdate(cacheKey, key => Tuple.Create(DateTime.Now, value), (key, old) => Tuple.Create(DateTime.Now, value));
			return value;
		}

		private static async Task<MostSimilarSubmissionsHistogramData> GetMostSimilarSubmissionsHistogramData(Guid taskId, int binsCount)
		{
			var mostSimilarSubmissions = await antiPlagiarismClient.GetMostSimilarSubmissionsAsync(new GetMostSimilarSubmissionsParameters
			{
				TaskId = taskId
			});
			var bins = new List<MostSimilarSubmissionsBin>();
			var step = 1.0 / binsCount;
			for (var i = 0; i < binsCount; i += 1)
			{
				var left = step * i;
				var right = left + step;
				if (right > 1 - 0.00001)
					right = 1.00001;
				var count = mostSimilarSubmissions.MostSimilarSubmissions
					.Count(s => s.Weight >= left && s.Weight < right);
				var bin = new MostSimilarSubmissionsBin
				{
					BinRightBorder = (decimal)Math.Round(right, 2),
					SubmissionsCount =  count
				};
				bins.Add(bin);
			}
			return new MostSimilarSubmissionsHistogramData
			{
				Bins = bins,
			};
		}

		private static MostSimilarSubmissionsHistogramData GetMostSimilarSubmissionsHistogramDataMock(int binsCount)
		{
			var bins = new List<MostSimilarSubmissionsBin>();
			var step = 1.0 / binsCount;
			var random = new Random(12);
			for (var i = 0; i < binsCount; i += 1)
			{
				var left = step * i;
				var right = left + step;
				if (right > 1 - 0.00001)
					right = 1.00001;
				var bin = new MostSimilarSubmissionsBin
				{
					BinRightBorder = (decimal)Math.Round(right, 2),
					SubmissionsCount = random.Next(0, 10)
				};
				bins.Add(bin);
			}
			return new MostSimilarSubmissionsHistogramData
			{
				Bins = bins,
			};
		}

		private static void RemoveOldValuesFromCache()
		{
			foreach (var key in plagiarismsCache.Keys.ToList())
			{
				if (plagiarismsCache.TryGetValue(key, out var cachedValue))
				{
					/* Remove cached value if it is too old */
					if (DateTime.Now.Subtract(cachedValue.Item1) > cacheLifeTime)
						plagiarismsCache.TryRemove(key, out _);
				}
			}
		}
	}

	[JsonConverter(typeof(StringEnumConverter), true)]
	public enum SuspicionLevel : short
	{
		None = 0,
		Faint = 1,
		Strong = 2,
	}

	[DataContract]
	public class AntiPlagiarismInfoModel
	{
		[DataMember(Name = "status")]
		public string Status { get; set; }

		[DataMember(Name = "suspicion_level")]
		public SuspicionLevel SuspicionLevel { get; set; }

		[DataMember(Name = "suspicious_authors_count")]
		public int SuspiciousAuthorsCount { get; set; }
	}

	public class AntiPlagiarismDetailsModel
	{
		public int SubmissionId { get; set; }

		public Course Course { get; set; }

		public Slide Slide { get; set; }

		public DefaultDictionary<string, string> UsersGroups { get; set; }

		public DefaultDictionary<string, string> UsersArchivedGroups { get; set; }

		public GetAuthorPlagiarismsResponse AntiPlagiarismResponse { get; set; }

		public Dictionary<int, UserExerciseSubmission> Submissions { get; set; }

		public MostSimilarSubmissionsHistogramData MostSimilarSubmissionsHistogramData { get; set; }

		public bool CanEditSuspicionLevels { get; set; }

		public SuspicionLevels SuspicionLevels { get; set; } // SuspicionLevels в AntiPlagiarismResponse кэшируются на несколько минут, а здесь актуальные

		public double MaxAuthorSubmissionWeight { get; set; }
	}

	public class MostSimilarSubmissionsHistogramData
	{
		public List<MostSimilarSubmissionsBin> Bins { get; set; }
	}

	public class MostSimilarSubmissionsBin
	{
		public decimal BinRightBorder { get; set; }
		public int SubmissionsCount { get; set; }
	}

	public class MockUserCanSeeAllGroups : IPrincipal
	{
		public bool IsInRole(string role)
		{
			return true;
		}

		public IIdentity Identity => new ClaimsIdentity();
	}
}