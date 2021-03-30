using Microsoft.AspNet.Identity;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Database;
using Database.DataContexts;
using Database.Extensions;
using Database.Models;
using Elmah;
using Vostok.Logging.Abstractions;
using LtiLibrary.Owin.Security.Lti;
using uLearn.Web.Extensions;
using uLearn.Web.FilterAttributes;
using uLearn.Web.LTI;
using uLearn.Web.Models;
using Ulearn.Common.Extensions;
using Ulearn.Core;
using Ulearn.Core.Courses;
using Ulearn.Core.Courses.Slides;
using Ulearn.Core.Courses.Slides.Exercises;
using Ulearn.Core.Courses.Slides.Exercises.Blocks;
using Ulearn.Core.Courses.Slides.Quizzes;
using Ulearn.Core.Courses.Units;

namespace uLearn.Web.Controllers
{
	[ULearnAuthorize]
	public class CourseController : BaseController
	{
		private static ILog log => LogProvider.Get().ForContext(typeof(CourseController));

		private readonly ULearnDb db = new ULearnDb();
		private readonly WebCourseManager courseManager = WebCourseManager.Instance;

		private readonly SlideRateRepo slideRateRepo;
		private readonly UserSolutionsRepo solutionsRepo;
		private readonly UnitsRepo unitsRepo;
		private readonly VisitsRepo visitsRepo;
		private readonly LtiRequestsRepo ltiRequestsRepo;
		private readonly SlideCheckingsRepo slideCheckingsRepo;
		private readonly GroupsRepo groupsRepo;
		private readonly UserQuizzesRepo userQuizzesRepo;
		private readonly CoursesRepo coursesRepo;
		private readonly TempCoursesRepo tempCoursesRepo;
		private readonly UserRolesRepo userRolesRepo;

		public CourseController()
		{
			slideCheckingsRepo = new SlideCheckingsRepo(db);
			visitsRepo = new VisitsRepo(db);
			unitsRepo = new UnitsRepo(db);
			slideRateRepo = new SlideRateRepo(db);
			solutionsRepo = new UserSolutionsRepo(db, courseManager);
			ltiRequestsRepo = new LtiRequestsRepo(db);
			groupsRepo = new GroupsRepo(db, courseManager);
			userQuizzesRepo = new UserQuizzesRepo(db);
			coursesRepo = new CoursesRepo(db);
			tempCoursesRepo = new TempCoursesRepo(db);
			userRolesRepo = new UserRolesRepo(db);
		}

		[AllowAnonymous]
		public async Task<ActionResult> SlideById(string courseId, string slideId = "", int? checkQueueItemId = null, int? version = null, int autoplay = 0)
		{
			if (slideId.Contains("_"))
				slideId = slideId.Substring(slideId.LastIndexOf('_') + 1);
			var userId = User.Identity.GetUserId();

			// По крайней мере одно из мест использования groupsIds: переход на следующее ревью после выполнения предыдущего.
			var groupsIds = Request.GetMultipleValuesFromQueryString("group");

			if (!Guid.TryParse(slideId, out var slideGuid))
				return HttpNotFound();

			if (string.IsNullOrWhiteSpace(courseId))
			{
				return RedirectToAction("Index", "Home");
			}

			var course = courseManager.FindCourse(courseId);
			if (course == null)
				return HttpNotFound();

			var visibleUnitIds = unitsRepo.GetVisibleUnitIds(course, userId);
			var visibleUnits = course.GetUnits(visibleUnitIds);
			var isGuest = !User.Identity.IsAuthenticated;
			var isInstructor = !isGuest && userRolesRepo.HasUserAccessToCourse(userId, courseId, CourseRole.Instructor);

			var slide = slideGuid == Guid.Empty
				? GetInitialSlideForStartup(courseId, visibleUnits, isInstructor)
				: course.FindSlideById(slideGuid, isInstructor);

			if (slide == null)
			{
				var instructorNote = course.FindInstructorNoteById(slideGuid);
				if (instructorNote != null && isInstructor)
					slide = instructorNote.Slide;
			}

			if (slide == null)
				return HttpNotFound();

			AbstractManualSlideChecking queueItem = null;
			var isManualCheckingReadonly = false;
			if (isInstructor && checkQueueItemId != null)
			{
				if (slide is QuizSlide)
					queueItem = slideCheckingsRepo.FindManualCheckingById<ManualQuizChecking>(checkQueueItemId.Value);
				if (slide is ExerciseSlide)
					queueItem = slideCheckingsRepo.FindManualCheckingById<ManualExerciseChecking>(checkQueueItemId.Value);

				if (queueItem == null)
				{
					/* It's possible when checking has not been fully checked, lock has been released, but after it user re-send his solution and we removed old waiting checking */
					var fakeQueueItem = slide is QuizSlide ? (AbstractManualSlideChecking)new ManualQuizChecking() : new ManualExerciseChecking();
					return RedirectToAction("CheckingQueue", "Admin", new
					{
						courseId = courseId,
						message = "checking_removed"
					});
				}
			}

			var model = isGuest ?
				CreateGuestCoursePageModel(course, slide, autoplay > 0) :
				await CreateCoursePageModel(course, slide, queueItem, version, groupsIds, autoplay > 0, isManualCheckingReadonly);

			if (!string.IsNullOrEmpty(Request.QueryString["error"]))
				model.Error = Request.QueryString["error"];

			if (!visibleUnits.Contains(model.Slide.Info.Unit))
				return HttpNotFound("Slide is hidden " + slideGuid);
			return View("Slide", model);
		}

		[AllowAnonymous]
		public async Task<ActionResult> Slide(string courseId)
		{
			var course = courseManager.FindCourse(courseId);
			if (course == null)
				return HttpNotFound();
			var userId = User.Identity.GetUserId();
			var visibleUnitIds = unitsRepo.GetVisibleUnitIds(course, userId);
			var visibleUnits = course.GetUnits(visibleUnitIds);
			var isInstructor = userRolesRepo.HasUserAccessToCourse(userId, course.Id, CourseRole.Instructor);
			var slide = GetInitialSlideForStartup(courseId, visibleUnits, isInstructor);
			if (slide == null)
				return HttpNotFound();
			return RedirectToRoute("Course.SlideById", new { courseId, slideId = slide.Url });
		}

		[AllowAnonymous]
		public async Task<ActionResult> LtiSlide(string courseId, Guid slideId)
		{
			if (string.IsNullOrWhiteSpace(courseId))
				return RedirectToAction("Index", "Home");

			var course = courseManager.GetCourse(courseId);
			var slide = course.GetSlideById(slideId, false);

			string userId;
			var owinRequest = Request.GetOwinContext().Request;
			if (await owinRequest.IsAuthenticatedLtiRequestAsync())
			{
				var ltiRequest = await owinRequest.ParseLtiRequestAsync();
				log.Info($"Нашёл LTI request в запросе: {ltiRequest.JsonSerialize()}");
				userId = Request.GetOwinContext().Authentication.AuthenticationResponseGrant.Identity.GetUserId();
				await ltiRequestsRepo.Update(courseId, userId, slide.Id, ltiRequest.JsonSerialize());

				/* Substitute http(s) scheme with real scheme from header */
				var uriBuilder = new UriBuilder(ltiRequest.Url)
				{
					Scheme = owinRequest.GetRealRequestScheme(),
					Port = owinRequest.GetRealRequestPort()
				};
				return Redirect(uriBuilder.Uri.AbsoluteUri);
			}

			/* For now user should be authenticated */
			if (!User.Identity.IsAuthenticated)
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			userId = User.Identity.GetUserId();
			var visit = await VisitSlide(courseId, slide.Id, userId);

			/* Try to send score via LTI immediately after slide visiting */
			try
			{
				if (visit.IsPassed)
					LtiUtils.SubmitScore(courseId, slide, userId, visit);
			}
			catch (Exception e)
			{
				ErrorLog.GetDefault(System.Web.HttpContext.Current).Log(new Error(e));
			}

			// Exercise обрабатывается реактом

			var quizSlide = slide as QuizSlide;
			if (quizSlide != null)
			{
				var model = new LtiQuizModel
				{
					CourseId = courseId,
					Slide = quizSlide,
					UserId = userId
				};
				return View("LtiQuizSlide", model);
			}

			return View();
		}

		private Slide GetInitialSlideForStartup(string courseId, List<Unit> orderedVisibleUnits, bool isInstructor)
		{
			var userId = User.Identity.GetUserId();
			var lastVisit = visitsRepo.FindLastVisit(courseId, userId);
			var orderedVisibleSlides = orderedVisibleUnits.SelectMany(u => u.GetSlides(isInstructor)).ToList();
			if (lastVisit != null)
			{
				var lastVisitedSlide = orderedVisibleSlides.FirstOrDefault(s => s.Id == lastVisit.SlideId);
				if (lastVisitedSlide != null)
					return lastVisitedSlide;
				if (isInstructor)
				{
					var instructorNoteSlide = orderedVisibleUnits.FirstOrDefault(u => u.Id == lastVisit.SlideId)?.InstructorNote?.Slide;
					if (instructorNoteSlide != null)
						return instructorNoteSlide;
				}
			}

			var unorderedVisitedIds = visitsRepo.GetIdOfVisitedSlides(courseId, userId);
			var lastVisitedVisibleSlide = orderedVisibleSlides.LastOrDefault(slide => unorderedVisitedIds.Contains(slide.Id));
			if (lastVisitedVisibleSlide != null)
				return lastVisitedVisibleSlide;
			return orderedVisibleSlides.Any() ? orderedVisibleSlides.First() : null;
		}

		private CoursePageModel CreateGuestCoursePageModel(Course course, Slide slide, bool autoplay)
		{
			return new CoursePageModel
			{
				CourseId = course.Id,
				CourseTitle = course.Title,
				Slide = slide,
				BlockRenderContext = new BlockRenderContext(
					course,
					slide,
					slide.Info.DirectoryRelativePath,
					slide.Blocks.Select(block => block is AbstractExerciseBlock ? new ExerciseBlockData(course.Id, (ExerciseSlide)slide, false) { Url = Url } : (dynamic)null).ToArray(),
					isGuest: true,
					autoplay: autoplay),
				IsGuest = true,
			};
		}

		private async Task<CoursePageModel> CreateCoursePageModel(
			Course course, Slide slide,
			AbstractManualSlideChecking manualChecking, int? exerciseSubmissionId = null,
			List<string> groupsIds = null,
			bool autoplay = false,
			bool isManualCheckingReadonly = false)
		{
			var userId = User.Identity.GetUserId();

			if (manualChecking != null)
				userId = manualChecking.UserId;

			var defaultProhibitFurtherReview = groupsRepo.GetDefaultProhibitFutherReviewForUser(course.Id, userId, userId);
			var manualCheckingsLeftInQueue = manualChecking != null ? ControllerUtils.GetManualCheckingsCountInQueue(slideCheckingsRepo, groupsRepo, userRolesRepo, userId, course.Id, slide, groupsIds) : 0;

			var (notArchivedGroupNames, archivedGroupNames) = GetGroupNames(course, manualChecking);

			var model = new CoursePageModel
			{
				UserId = userId,
				CourseId = course.Id,
				CourseTitle = course.Title,
				Slide = slide,
				BlockRenderContext = CreateRenderContext(
					course, slide, manualChecking, exerciseSubmissionId, groupsIds,
					autoplay: autoplay,
					isManualCheckingReadonly: isManualCheckingReadonly,
					defaultProhibitFurtherReview: defaultProhibitFurtherReview, manualCheckingsLeftInQueue: manualCheckingsLeftInQueue),
				ManualChecking = manualChecking,
				ContextManualCheckingUserGroups = notArchivedGroupNames,
				ContextManualCheckingUserArchivedGroups = archivedGroupNames,
				IsGuest = false,
			};
			return model;
		}

		private (string, string) GetGroupNames(Course course, AbstractManualSlideChecking manualChecking)
		{
			var userId = User.Identity.GetUserId();
			var notArchivedGroupNames = "";
			var archivedGroupNames = "";
			if (manualChecking != null)
			{
				var userGroups = groupsRepo.GetUsersGroups(new List<string> { course.Id }, new List<string> { manualChecking.UserId }, userId,
					actual: true, archived: true, 100);
				if (userGroups.ContainsKey(manualChecking.UserId))
				{
					notArchivedGroupNames = string.Join(", ", groupsRepo.GetUserGroupsNames(userGroups[manualChecking.UserId].Where(g => !g.IsArchived)));
					archivedGroupNames = string.Join(", ", groupsRepo.GetUserGroupsNames(userGroups[manualChecking.UserId].Where(g => g.IsArchived)));
				}
			}

			return (notArchivedGroupNames, archivedGroupNames);
		}

		// returns null if user can't edit git
		private string GetGitEditLink(Course course, FileInfo pageFile)
		{
			var userId = User.Identity.GetUserId();
			var courseRole = userRolesRepo.GetRole(userId, course.Id);
			var canEditGit = courseRole != null && courseRole <= CourseRole.CourseAdmin;
			if (!canEditGit)
				return null;
			var publishedCourseVersion = coursesRepo.GetPublishedCourseVersion(course.Id);
			if (publishedCourseVersion?.RepoUrl == null)
				return null;
			var pathRelative2CourseXml = pageFile.FullName.Substring(course.CourseXmlDirectory.FullName.Length + 1);
			if (publishedCourseVersion.PathToCourseXml == null)
				return null;
			return GitUtils.GetSlideEditLink(publishedCourseVersion.RepoUrl, publishedCourseVersion.PathToCourseXml, pathRelative2CourseXml);
		}

		private int GetMaxSlideScoreForUser(Course course, Slide slide, string userId)
		{
			var isSlideSolved = ControllerUtils.IsSlideSolved(solutionsRepo, userQuizzesRepo, course, userId, slide.Id);
			var hasManualChecking = visitsRepo.HasManualChecking(course.Id, userId, slide.Id);
			var enabledManualCheckingForUser = groupsRepo.IsManualCheckingEnabledForUser(course, userId);
			var maxSlideScore = ControllerUtils.GetMaxScoreForUsersSlide(slide, isSlideSolved, hasManualChecking, enabledManualCheckingForUser);
			return maxSlideScore;
		}

		private BlockRenderContext CreateRenderContext(Course course, Slide slide,
			AbstractManualSlideChecking manualChecking = null,
			int? exerciseSubmissionId = null, List<string> groupsIds = null, bool isLti = false,
			bool autoplay = false, bool isManualCheckingReadonly = false, bool defaultProhibitFurtherReview = true,
			int manualCheckingsLeftInQueue = 0)
		{
			var userId = User.Identity.GetUserId();
			/* ExerciseController will fill blockDatas later */
			var blockData = slide.Blocks.Select(b => (dynamic)null).ToArray();
			return new BlockRenderContext(
				course,
				slide,
				slide.Info.DirectoryRelativePath,
				blockData,
				isGuest: false,
				revealHidden:userRolesRepo.HasUserAccessToCourse(userId, course.Id, CourseRole.Instructor),
				manualChecking: manualChecking,
				manualCheckingsLeftInQueue: manualCheckingsLeftInQueue,
				canUserFillQuiz: false,
				groupsIds: groupsIds,
				isLti: isLti,
				autoplay: autoplay,
				isManualCheckingReadonly: isManualCheckingReadonly,
				defaultProhibitFurtherReview: defaultProhibitFurtherReview
			)
			{
				VersionId = exerciseSubmissionId
			};
		}

		public async Task<ActionResult> AcceptedSolutions(string courseId, Guid slideId, bool isLti = false)
		{
			var userId = User.Identity.GetUserId();
			var course = courseManager.GetCourse(courseId);
			var isInstructor = userRolesRepo.HasUserAccessToCourse(userId, course.Id, CourseRole.Instructor);
			var slide = course.GetSlideById(slideId, isInstructor) as ExerciseSlide;
			if (slide == null)
				return HttpNotFound();

			// Test redirect to SlideId if disabled
			if (slide.Exercise.HideShowSolutionsButton)
				return RedirectToRoute("Course.SlideById", new { courseId = course.Id, slideId = slide.Url });
			var model = await CreateAcceptedSolutionsModel(course, slide, isLti);
			return View("AcceptedSolutions", model);
		}

		private async Task<AcceptedSolutionsPageModel> CreateAcceptedSolutionsModel(Course course, ExerciseSlide slide, bool isLti)
		{
			var userId = User.Identity.GetUserId();
			var isPassed = visitsRepo.IsPassed(course.Id, slide.Id, userId);
			if (!isPassed)
				await visitsRepo.SkipSlide(course.Id, slide.Id, userId);
			var submissions = solutionsRepo.GetBestTrendingAndNewAcceptedSolutions(course.Id, slide.Id);
			foreach (var submission in submissions)
			{
				submission.LikedAlready = submission.UsersWhoLike.Any(u => u == userId);
			}

			var model = new AcceptedSolutionsPageModel
			{
				CourseId = course.Id,
				CourseTitle = course.Title,
				Slide = slide,
				AcceptedSolutions = submissions,
				User = User,
				LikeSolutionUrl = Url.Action("LikeSolution"),
				IsLti = isLti,
				IsPassed = isPassed
			};
			return model;
		}

		[AllowAnonymous]
		public async Task<ActionResult> AcceptedAlert(string courseId, Guid slideId)
		{
			var owinRequest = Request.GetOwinContext().Request;
			if (await owinRequest.IsAuthenticatedLtiRequestAsync())
			{
				var ltiRequest = await owinRequest.ParseLtiRequestAsync();
				/* Substitute http(s) scheme with real scheme from header */
				var uriBuilder = new UriBuilder(ltiRequest.Url)
				{
					Scheme = owinRequest.GetRealRequestScheme(),
					Port = owinRequest.GetRealRequestPort()
				};
				return Redirect(uriBuilder.Uri.AbsoluteUri);
			}

			/* For now user should be authenticated */
			if (!User.Identity.IsAuthenticated)
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			var userId = User.Identity.GetUserId();
			var course = courseManager.GetCourse(courseId);
			var isInstructor = userRolesRepo.HasUserAccessToCourse(userId, course.Id, CourseRole.Instructor);
			var slide = (ExerciseSlide)course.GetSlideById(slideId, isInstructor);
			var model = CreateAcceptedAlertModel(slide, course);
			return View(model);
		}

		private ExerciseBlockData CreateAcceptedAlertModel(ExerciseSlide slide, Course course)
		{
			var userId = User.Identity.GetUserId();
			var isSkippedOrPassed = visitsRepo.IsSkippedOrPassed(course.Id, slide.Id, userId);
			/* TODO: It's not necessary to create ExerciseBlockData here */
			var model = new ExerciseBlockData(course.Id, slide, isSkippedOrPassed)
			{
				CourseId = course.Id,
				IsGuest = !User.Identity.IsAuthenticated,
				Url = Url,
			};
			return model;
		}

		/* Slide rating don't used anymore */
		[HttpPost]
		public async Task<string> ApplyRate(string courseId, Guid slideId, string rate)
		{
			var userId = User.Identity.GetUserId();
			var slideRate = (SlideRates)Enum.Parse(typeof(SlideRates), rate);
			return await slideRateRepo.AddRate(courseId, slideId, userId, slideRate);
		}

		[HttpPost]
		public string GetRate(string courseId, Guid slideId)
		{
			var userId = User.Identity.GetUserId();
			return slideRateRepo.FindRate(courseId, slideId, userId);
		}

		[HttpPost]
		public async Task<JsonResult> LikeSolution(int solutionId)
		{
			var res = await solutionsRepo.Like(solutionId, User.Identity.GetUserId());
			return Json(new { likesCount = res.Item1, liked = res.Item2 });
		}

		public async Task<Visit> VisitSlide(string courseId, Guid slideId, string userId)
		{
			if (string.IsNullOrEmpty(userId))
				return null;
			await visitsRepo.AddVisit(courseId, slideId, userId, GetRealClientIp());
			return visitsRepo.FindVisit(courseId, slideId, userId);
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		public ActionResult InstructorNote(string courseId, Guid unitId)
		{
			var course = courseManager.GetCourse(courseId);
			var instructorNote = course.GetUnitByIdNotSafe(unitId).InstructorNote;
			if (instructorNote == null)
				return HttpNotFound("No instructor note for this unit");
			var gitEditUrl = GetGitEditLink(course, instructorNote.File);
			return View(new IntructorNoteModel(courseId, instructorNote, gitEditUrl));
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.Tester)]
		public async Task<ActionResult> ForgetAll(string courseId, Guid slideId)
		{
			var slide = courseManager.GetCourse(courseId).GetSlideById(slideId, true);
			var userId = User.Identity.GetUserId();
			db.SolutionLikes.RemoveRange(db.SolutionLikes.Where(q => q.UserId == userId && q.Submission.SlideId == slideId));

			db.UserExerciseSubmissions.RemoveSlideAction(courseId, slideId, userId);
			db.UserQuizSubmissions.RemoveSlideAction(courseId, slideId, userId);
			db.Visits.RemoveSlideAction(courseId, slideId, userId);
			await slideCheckingsRepo.RemoveAttempts(courseId, slideId, userId, false);

			db.UserQuestions.RemoveSlideAction(courseId, slideId, userId);
			db.SlideRates.RemoveSlideAction(courseId, slideId, userId);
			db.Hints.RemoveSlideAction(courseId, slideId, userId);
			await db.SaveChangesAsync();

			return RedirectToAction("SlideById", new { courseId, slideId = slide.Id });
		}

		public async Task<ActionResult> Courses(string courseId = null, string courseTitle = null)
		{
			var userId = User.Identity.GetUserId();
			var isSystemAdministrator = userRolesRepo.IsSystemAdministrator(userId);
			var courses = courseManager.GetCourses();

			// Неопубликованные курсы не покажем тем, кто не имеет роли в них.
			if (!isSystemAdministrator)
			{
				var visibleCourses = unitsRepo.GetVisibleCourses();
				var coursesInWhichUserHasAnyRole = userRolesRepo.GetCoursesWhereUserIsInRole(userId, CourseRole.Tester);
				var coursesWhereIAmStudent = groupsRepo.GetUserGroups(userId)
					.Select(g => g.CourseId)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.Where(c => visibleCourses.Contains(c)).ToList();
				courses = courses.Where(c => coursesInWhichUserHasAnyRole.Contains(c.Id, StringComparer.OrdinalIgnoreCase)
					|| coursesWhereIAmStudent.Contains(c.Id, StringComparer.OrdinalIgnoreCase));
			}

			var incorrectChars = new string(CourseManager.GetInvalidCharacters().OrderBy(c => c).Where(c => 32 <= c).ToArray());
			if (isSystemAdministrator)
				courses = courses.OrderBy(course => course.Id, StringComparer.InvariantCultureIgnoreCase);
			else
				courses = courses.OrderBy(course => course.Title, StringComparer.InvariantCultureIgnoreCase);

			var tempCourses = tempCoursesRepo.GetTempCourses().Select(c => c.CourseId).ToHashSet();
			var model = new CourseListViewModel
			{
				Courses = courses
					.Select(course => new CourseViewModel
					{
						Id = course.Id,
						Title = course.Title,
						LastWriteTime = courseManager.GetLastWriteTime(course.Id),
						IsTemp = tempCourses.Contains(course.Id)
					})
					.ToList(),
				LastTryCourseId = courseId,
				LastTryCourseTitle = courseTitle,
				InvalidCharacters = incorrectChars
			};
			return View(model);
		}
	}
}