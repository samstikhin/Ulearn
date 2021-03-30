using Vostok.Logging.Abstractions;
using Microsoft.AspNet.Identity;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using AntiPlagiarism.Api;
using AntiPlagiarism.Api.Models.Parameters;
using ApprovalUtilities.Utilities;
using Database;
using Database.DataContexts;
using Database.Models;
using GitCourseUpdater;
using Microsoft.VisualBasic.FileIO;
using uLearn.Web.Extensions;
using uLearn.Web.FilterAttributes;
using uLearn.Web.Models;
using Ulearn.Common.Extensions;
using Ulearn.Core;
using Ulearn.Core.Configuration;
using Ulearn.Core.Courses;
using Ulearn.Core.Courses.Slides;
using Ulearn.Core.Courses.Slides.Exercises;
using Ulearn.Core.Courses.Units;
using Ulearn.Core.CSharp;
using Ulearn.Core.Extensions;

namespace uLearn.Web.Controllers
{
	[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
	public class AdminController : Controller
	{
		private static ILog log => LogProvider.Get().ForContext(typeof(AdminController));

		private readonly WebCourseManager courseManager = WebCourseManager.Instance;
		private readonly ULearnDb db;
		private readonly UsersRepo usersRepo;
		private readonly UserRolesRepo userRolesRepo;
		private readonly CommentsRepo commentsRepo;
		private readonly UserManager<ApplicationUser> userManager;
		private readonly CoursesRepo coursesRepo;
		private readonly GroupsRepo groupsRepo;
		private readonly SlideCheckingsRepo slideCheckingsRepo;
		private readonly UserSolutionsRepo userSolutionsRepo;
		private readonly CertificatesRepo certificatesRepo;
		private readonly AdditionalScoresRepo additionalScoresRepo;
		private readonly NotificationsRepo notificationsRepo;
		private readonly SystemAccessesRepo systemAccessesRepo;
		private readonly StyleErrorsRepo styleErrorsRepo;
		private readonly CertificateGenerator certificateGenerator;
		private readonly string gitSecret;
		private readonly DirectoryInfo reposDirectory;
		private readonly IAntiPlagiarismClient antiPlagiarismClient;
		private readonly TempCoursesRepo tempCoursesRepo;

		public AdminController()
		{
			db = new ULearnDb();

			usersRepo = new UsersRepo(db);
			userRolesRepo = new UserRolesRepo(db);
			commentsRepo = new CommentsRepo(db);
			userManager = new ULearnUserManager(db);
			coursesRepo = new CoursesRepo(db);
			groupsRepo = new GroupsRepo(db, courseManager);
			slideCheckingsRepo = new SlideCheckingsRepo(db);
			userSolutionsRepo = new UserSolutionsRepo(db, courseManager);
			certificatesRepo = new CertificatesRepo(db);
			additionalScoresRepo = new AdditionalScoresRepo(db);
			notificationsRepo = new NotificationsRepo(db);
			systemAccessesRepo = new SystemAccessesRepo(db);
			styleErrorsRepo = new StyleErrorsRepo(db);
			certificateGenerator = new CertificateGenerator(db, courseManager);
			tempCoursesRepo = new TempCoursesRepo(db);
			reposDirectory = CourseManager.GetCoursesDirectory().GetSubdirectory("Repos");
			var configuration = ApplicationConfiguration.Read<UlearnConfiguration>();
			gitSecret = configuration.Git.Webhook.Secret;
			var antiplagiarismClientConfiguration = ApplicationConfiguration.Read<UlearnConfiguration>().AntiplagiarismClient;
			antiPlagiarismClient = new AntiPlagiarismClient(antiplagiarismClientConfiguration.Endpoint, antiplagiarismClientConfiguration.Token);
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public ActionResult SpellingErrors(Guid versionId)
		{
			var course = courseManager.GetVersion(versionId);
			courseManager.EnsureVersionIsExtracted(versionId);
			return PartialView(course.SpellCheck());
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public ActionResult Units(string courseId)
		{
			var course = courseManager.GetCourse(courseId);
			var appearances = db.UnitAppearances.Where(u => u.CourseId == course.Id).ToList();
			var unitAppearances = course.GetUnitsNotSafe()
				.Select(unit => Tuple.Create(unit, appearances.FirstOrDefault(a => a.UnitId == unit.Id)))
				.ToList();
			return View(new UnitsListViewModel(course.Id, course.Title, unitAppearances, DateTime.Now));
		}

		[HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<RedirectToRouteResult> SetPublishTime(string courseId, Guid unitId, string publishTime)
		{
			var oldInfo = await db.UnitAppearances.Where(u => u.CourseId == courseId && u.UnitId == unitId).ToListAsync();
			db.UnitAppearances.RemoveRange(oldInfo);
			var unitAppearance = new UnitAppearance
			{
				CourseId = courseId,
				UnitId = unitId,
				UserName = User.Identity.Name,
				PublishTime = DateTime.Parse(publishTime),
			};
			db.UnitAppearances.Add(unitAppearance);
			await db.SaveChangesAsync();
			return RedirectToAction("Units", new { courseId });
		}

		[HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<RedirectToRouteResult> RemovePublishTime(string courseId, Guid unitId)
		{
			var unitAppearance = await db.UnitAppearances.FirstOrDefaultAsync(u => u.CourseId == courseId && u.UnitId == unitId);
			if (unitAppearance != null)
			{
				db.UnitAppearances.Remove(unitAppearance);
				await db.SaveChangesAsync();
			}

			return RedirectToAction("Units", new { courseId });
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public ActionResult DownloadPackage(string courseId)
		{
			var packageName = courseManager.GetPackageName(courseId);
			var path = courseManager.GetStagingCoursePath(courseId);
			var content = System.IO.File.ReadAllBytes(path);
			return File(content, "application/zip", packageName);
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public ActionResult DownloadVersion(string courseId, Guid versionId)
		{
			var packageName = courseManager.GetPackageName(courseId);
			return File(courseManager.GetCourseVersionFile(versionId).FullName, "application/zip", packageName);
		}

		private async Task NotifyAboutCourseVersion(string courseId, Guid versionId, string userId)
		{
			var notification = new UploadedPackageNotification
			{
				CourseVersionId = versionId,
			};
			await notificationsRepo.AddNotification(courseId, notification, userId);
		}

		private async Task NotifyAboutCourseUploadFromRepoError(string courseId, string commitHash, string repoUrl)
		{
			var notification = new NotUploadedPackageNotification
			{
				CommitHash = commitHash,
				RepoUrl = repoUrl
			};
			var bot = usersRepo.GetUlearnBotUser();
			await notificationsRepo.AddNotification(courseId, notification, bot.Id);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> SaveCourseRepoSettings(string courseId, string repoUrl, string branch, string pathToCourseXml, bool isWebhookEnabled, string submitButton)
		{
			if (submitButton == "Save")
			{
				repoUrl = repoUrl.NullIfEmptyOrWhitespace();
				pathToCourseXml = pathToCourseXml.NullIfEmptyOrWhitespace();
				branch = branch.NullIfEmptyOrWhitespace() ?? "master";
				var oldRepoSettings = coursesRepo.GetCourseRepoSettings(courseId);
				var settings = oldRepoSettings != null && oldRepoSettings.RepoUrl == repoUrl ? oldRepoSettings : new CourseGit { CourseId = courseId };
				settings.RepoUrl = repoUrl;
				settings.PathToCourseXml = pathToCourseXml;
				settings.Branch = branch;
				settings.IsWebhookEnabled = isWebhookEnabled;
				if (settings.PrivateKey == null && repoUrl != null)
				{
					var coursesWithSameRepo = coursesRepo.FindCoursesByRepoUrl(repoUrl).Where(r => r.PrivateKey != null).ToList();
					if (coursesWithSameRepo.Any())
					{
						settings.PrivateKey = coursesWithSameRepo[0].PrivateKey;
						settings.PublicKey = coursesWithSameRepo[0].PublicKey;
					}
					else
					{
						var keys = SshKeyGenerator.Generate();
						settings.PrivateKey = keys.PrivatePEM;
						settings.PublicKey = keys.PublicSSH;
					}
				}

				await coursesRepo.SetCourseRepoSettings(settings).ConfigureAwait(false);
				return PackagesInternal(courseId, openStep1: true, openStep2: true);
			}

			if (submitButton == "Remove")
			{
				await coursesRepo.RemoveCourseRepoSettings(courseId).ConfigureAwait(false);
			}

			return RedirectToAction("Packages", new { courseId });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> GenerateCourseRepoKey(string courseId, string repoUrl)
		{
			var keys = SshKeyGenerator.Generate();
			await coursesRepo.UpdateKeysByRepoUrl(repoUrl, keys.PublicSSH, keys.PrivatePEM).ConfigureAwait(false);
			return PackagesInternal(courseId, openStep1: true, openStep2: true);
		}

		[HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> UploadCourse(string courseId, HttpPostedFileBase file)
		{
			if (file == null || file.ContentLength <= 0)
				return RedirectToAction("Packages", new { courseId });

			var fileName = Path.GetFileName(file.FileName);
			if (fileName == null || !fileName.ToLower().EndsWith(".zip"))
				return RedirectToAction("Packages", new { courseId });

			var (versionId, error) = await UploadCourse(courseId, file.InputStream, User.Identity.GetUserId()).ConfigureAwait(false);
			if (error != null)
			{
				var errorMessage = error.Message.ToLowerFirstLetter();
				while (error.InnerException != null)
				{
					errorMessage += $"\n\n{error.InnerException.Message}";
					error = error.InnerException;
				}

				return Packages(courseId, errorMessage);
			}

			return RedirectToAction("Diagnostics", new { courseId, versionId });
		}

		public async Task UploadCoursesWithGit(string repoUrl, string branch)
		{
			var courses = coursesRepo.FindCoursesByRepoUrl(repoUrl).Where(r => r.IsWebhookEnabled && (r.Branch == branch || branch == "master" && r.Branch == null)).ToList();
			if (courses.Count == 0)
			{
				log.Warn($"Repo '{repoUrl}' is not expected");
				return;
			}

			log.Info($"Start update repo '{repoUrl}'");
			var userId = usersRepo.GetUlearnBotUser().Id;
			var publicKey = courses[0].PublicKey; // у всех курсов одинаковый repoUrl и ключ
			var privateKey = courses[0].PrivateKey;
			var infoForUpload = new List<(string CourseId, MemoryStream Zip, CommitInfo CommitInfo, string PathToCourseXml)>();
			try
			{
				using (IGitRepo git = new GitRepo(repoUrl, reposDirectory, publicKey, privateKey, new DirectoryInfo(Path.GetTempPath())))
				{ // В GitRepo используется Monitor. Он должен быть освобожден в том же потоке, что и взят.
					git.Checkout(branch);
					var commitInfo = git.GetCurrentCommitInfo();
					foreach (var courseRepo in courses)
					{
						var zip = git.GetCurrentStateAsZip(courseRepo.PathToCourseXml);
						var hasChanges = true;
						if (courses.Count > 1)
						{
							var publishedVersion = coursesRepo.GetPublishedCourseVersion(courseRepo.CourseId);
							if (publishedVersion?.CommitHash != null)
							{
								var changedFiles = git.GetChangedFiles(publishedVersion.CommitHash, commitInfo.Hash, courseRepo.PathToCourseXml);
								hasChanges = changedFiles?.Any() ?? true;
							}
						}

						if (hasChanges)
						{
							log.Info($"Course '{courseRepo.CourseId}' has changes in '{repoUrl}'");
							infoForUpload.Add((courseRepo.CourseId, zip, commitInfo, courseRepo.PathToCourseXml));
						}
						else
						{
							log.Info($"Course '{courseRepo.CourseId}' has not changes in '{repoUrl}'");
						}
					}
				}

				foreach (var info in infoForUpload)
				{
					var (courseId, zip, commitInfo, pathToCourseXml) = info;
					var (_, error) = await UploadCourse(courseId, zip, userId, repoUrl, commitInfo, pathToCourseXml).ConfigureAwait(false);
					if (error != null)
						await NotifyAboutCourseUploadFromRepoError(courseId, commitInfo.Hash, repoUrl).ConfigureAwait(false);
				}
			}
			finally
			{
				infoForUpload.ForEach(i => i.Zip.Dispose());
			}
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> UploadCourseWithGit(string courseId)
		{
			var courseRepo = coursesRepo.GetCourseRepoSettings(courseId);
			if (courseRepo == null)
				return RedirectToAction("Packages", new { courseId, error = "Course repo settings not found" });

			var publicKey = courseRepo.PublicKey; // у всех курсов одинаковый repoUrl и ключ
			var privateKey = courseRepo.PrivateKey;

			Exception error = null;
			MemoryStream zip = null;
			CommitInfo commitInfo = null;
			try
			{
				using (IGitRepo git = new GitRepo(courseRepo.RepoUrl, reposDirectory, publicKey, privateKey, new DirectoryInfo(Path.GetTempPath())))
				{
					git.Checkout(courseRepo.Branch);
					commitInfo = git.GetCurrentCommitInfo();
					zip = git.GetCurrentStateAsZip(courseRepo.PathToCourseXml); 
				}
			}
			catch (GitException ex)
			{
				if (ex.MayBeSSHException)
				{
					log.Error(ex.InnerException);
					error = new Exception("Не удалось получить данные из репозитория. Вероятно не настроен деплой ключ. Исходный текст ошибки:", ex.InnerException);
				}
				else
					throw;
			}

			var versionId = new Guid();
			if (error == null)
				using (zip)
					(versionId, error) = await UploadCourse(courseId, zip, User.Identity.GetUserId(), courseRepo.RepoUrl, commitInfo, courseRepo.PathToCourseXml);

			if (error != null)
			{
				var errorMessage = error.Message.ToLowerFirstLetter();
				while (error.InnerException != null)
				{
					errorMessage += $"\n\n{error.InnerException.Message}";
					error = error.InnerException;
				}

				return Packages(courseId, errorMessage);
			}

			return RedirectToAction("Diagnostics", new { courseId, versionId });
		}


		private async Task<(Guid versionId, Exception error)> UploadCourse(string courseId, Stream content, string userId,
			string uploadedFromRepoUrl = null, CommitInfo commitInfo = null, string pathToCourseXmlInRepo = null)
		{
			log.Info($"Start upload course '{courseId}'");
			var versionId = Guid.NewGuid();

			var destinationFile = courseManager.GetCourseVersionFile(versionId);
			using (var file = new FileStream(destinationFile.FullName, FileMode.Create, FileAccess.Write))
				await content.CopyToAsync(file);
			Course updatedCourse;
			try
			{
				/* Load version and put it into LRU-cache */
				updatedCourse = courseManager.GetVersion(versionId);
			}
			catch (Exception e)
			{
				log.Warn(e, $"Upload course exception '{courseId}'");
				return (versionId, e);
			}

			log.Info($"Successfully update course files '{courseId}'");
			if (pathToCourseXmlInRepo == null && uploadedFromRepoUrl != null)
			{
				var extractedVersionDirectory = courseManager.GetExtractedVersionDirectory(versionId);
				pathToCourseXmlInRepo = extractedVersionDirectory.FullName == updatedCourse.CourseXmlDirectory.FullName
					? ""
					: updatedCourse.CourseXmlDirectory.FullName.Substring(extractedVersionDirectory.FullName.Length + 1);
			}

			await coursesRepo.AddCourseVersion(courseId, versionId, userId,
				pathToCourseXmlInRepo, uploadedFromRepoUrl, commitInfo?.Hash, commitInfo?.Message);
			await NotifyAboutCourseVersion(courseId, versionId, userId);
			try
			{
				var courseVersions = coursesRepo.GetCourseVersions(courseId);
				var previousUnpublishedVersions = courseVersions.Where(v => v.PublishTime == null && v.Id != versionId).ToList();
				foreach (var unpublishedVersion in previousUnpublishedVersions)
					await DeleteVersion(courseId, unpublishedVersion.Id).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				log.Warn(ex, "Error during delete previous unpublished versions");
			}

			return (versionId, null);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[ULearnAuthorize(ShouldBeSysAdmin = true)]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> CreateCourse(string courseId, string courseTitle)
		{
			var versionId = Guid.NewGuid();

			if (!courseManager.TryCreateCourse(courseId, courseTitle, versionId))
				return RedirectToAction("Courses", "Course",  new { courseId = courseId, courseTitle = courseTitle });

			var userId = User.Identity.GetUserId();
			await coursesRepo.AddCourseVersion(courseId, versionId, userId, null, null, null, null).ConfigureAwait(false);
			await coursesRepo.MarkCourseVersionAsPublished(versionId).ConfigureAwait(false);
			var courseFile = courseManager.GetStagingCourseFile(courseId);
			await coursesRepo.AddCourseFile(courseId, versionId, courseFile.ReadAllContent()).ConfigureAwait(false);
			await NotifyAboutPublishedCourseVersion(courseId, versionId, userId).ConfigureAwait(false);

			return RedirectToAction("Packages", new { courseId, onlyPrivileged = true });
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		[ValidateInput(false)]
		public ActionResult Packages(string courseId, string error = "")
		{
			var isTempCourse = tempCoursesRepo.Find(courseId) != null;
			if (isTempCourse)
				return null;
			return PackagesInternal(courseId, error);
		}

		private ActionResult PackagesInternal(string courseId, string error = "", bool openStep1 = false, bool openStep2 = false)
		{
			var hasPackage = courseManager.HasPackageFor(courseId);
			var lastUpdate = courseManager.GetLastWriteTime(courseId);
			var course = courseManager.GetCourse(courseId);
			var courseVersions = coursesRepo.GetCourseVersions(courseId).ToList();
			var publishedVersion = coursesRepo.GetPublishedCourseVersion(courseId);
			var courseRepo = coursesRepo.GetCourseRepoSettings(courseId);
			return View("Packages", model: new PackagesViewModel
			{
				Course = course,
				HasPackage = hasPackage,
				LastUpdate = lastUpdate,
				Versions = courseVersions,
				PublishedVersion = publishedVersion,
				CourseGit = courseRepo,
				OpenStep1 = openStep1,
				OpenStep2 = openStep2,
				GitSecret = gitSecret,
				Error = error,
			});
		}

		public ActionResult Comments(string courseId)
		{
			const int commentsCountLimit = 500;

			var userId = User.Identity.GetUserId();

			var course = courseManager.GetCourse(courseId);
			var commentsPolicy = commentsRepo.GetCommentsPolicy(courseId);

			var comments = commentsRepo.GetCourseComments(courseId)
				.Where(c => !c.IsForInstructorsOnly)
				.OrderByDescending(x => x.PublishTime)
				.ToList();
			var commentsLikes = commentsRepo.GetCommentsLikesCounts(comments);
			var commentsLikedByUser = commentsRepo.GetCourseCommentsLikedByUser(courseId, userId);
			var commentsById = comments.ToDictionary(x => x.Id);

			var canViewProfiles = systemAccessesRepo.HasSystemAccess(userId, SystemAccessType.ViewAllProfiles) || userRolesRepo.IsSystemAdministrator(userId);

			return View(new AdminCommentsViewModel
			{
				CourseId = courseId,
				IsCommentsEnabled = commentsPolicy.IsCommentsEnabled,
				ModerationPolicy = commentsPolicy.ModerationPolicy,
				OnlyInstructorsCanReply = commentsPolicy.OnlyInstructorsCanReply,
				Comments = (from c in comments.Take(commentsCountLimit)
					let slide = course.FindSlideById(c.SlideId, true)
					where slide != null
					select
						new CommentViewModel
						{
							Comment = c,
							LikesCount = commentsLikes.GetOrDefault(c.Id),
							IsLikedByUser = commentsLikedByUser.Contains(c.Id),
							Replies = new List<CommentViewModel>(),
							CanEditAndDeleteComment = true,
							CanModerateComment = true,
							IsCommentVisibleForUser = true,
							ShowContextInformation = true,
							ContextSlideTitle = slide.Title,
							ContextParentComment = c.IsTopLevel() ? null : commentsById.ContainsKey(c.ParentCommentId) ? commentsById[c.ParentCommentId].Text : null,
							CanViewAuthorProfile = canViewProfiles,
						}).ToList()
			});
		}

		private ManualCheckingQueueFilterOptions GetManualCheckingFilterOptionsByGroup(string courseId, List<string> groupsIds)
		{
			return ControllerUtils.GetFilterOptionsByGroup<ManualCheckingQueueFilterOptions>(groupsRepo, userRolesRepo, User.Identity.GetUserId(), courseId, groupsIds);
		}

		/* Returns merged checking queue for exercises (code reviews) as well as for quizzes */
		private List<AbstractManualSlideChecking> GetMergedCheckingQueue(ManualCheckingQueueFilterOptions filterOptions)
		{
			var result = slideCheckingsRepo.GetManualCheckingQueue<ManualExerciseChecking>(filterOptions).Cast<AbstractManualSlideChecking>().ToList();
			result.AddRange(slideCheckingsRepo.GetManualCheckingQueue<ManualQuizChecking>(filterOptions));

			result = result.OrderByDescending(c => c.Timestamp).ToList();
			if (filterOptions.Count > 0)
				result = result.Take(filterOptions.Count).ToList();

			return result;
		}
		
		private HashSet<Guid> GetMergedCheckingQueueSlideIds(ManualCheckingQueueFilterOptions filterOptions)
		{
			var result = slideCheckingsRepo.GetManualCheckingQueueSlideIds<ManualExerciseChecking>(filterOptions);
			result.UnionWith(slideCheckingsRepo.GetManualCheckingQueueSlideIds<ManualQuizChecking>(filterOptions));
			return result;
		}

		private ActionResult InternalCheckingQueue(string courseId, bool done, List<string> groupsIds, string userId = "", Guid? slideId = null, string message = "")
		{
			const int maxShownQueueSize = 500;
			var course = courseManager.GetCourse(courseId);

			var filterOptions = GetManualCheckingFilterOptionsByGroup(courseId, groupsIds);
			if (filterOptions.UserIds == null)
				groupsIds = new List<string> { "all" };

			if (!string.IsNullOrEmpty(userId))
				filterOptions.UserIds = new List<string> { userId };
			if (slideId.HasValue)
				filterOptions.SlidesIds = new List<Guid> { slideId.Value };

			filterOptions.OnlyChecked = done;
			filterOptions.Count = maxShownQueueSize + 1;
			var checkings = GetMergedCheckingQueue(filterOptions);

			if (!checkings.Any() && !string.IsNullOrEmpty(message))
				return RedirectToAction("CheckingQueue", new { courseId, group = string.Join(",", groupsIds) });

			var groups = groupsRepo.GetAvailableForUserGroups(courseId, User.Identity.GetUserId());
			
			var alreadyChecked = done;
			Dictionary<int, List<ExerciseCodeReview>> reviews = null;
			Dictionary<int, string> solutions = null;
			if (alreadyChecked)
			{
				reviews = slideCheckingsRepo.GetExerciseCodeReviewForCheckings(checkings.Select(c => c.Id));
				var submissionsIds = checkings.Select(c => (c as ManualExerciseChecking)?.SubmissionId).Where(s => s.HasValue).Select(s => s.Value);
				solutions = userSolutionsRepo.GetSolutionsForSubmissions(submissionsIds);
			}

			var allCheckingsSlides = GetAllCheckingsSlides(course, groupsIds, filterOptions);

			return View("CheckingQueue", new ManualCheckingQueueViewModel
			{
				CourseId = courseId,
				/* TODO (andgein): Merge FindSlideById() and following GetSlideById() calls */
				Checkings = checkings.Take(maxShownQueueSize).Where(c => course.FindSlideById(c.SlideId, true) != null).Select(c =>
				{
					var slide = course.GetSlideById(c.SlideId, true);
					return new ManualCheckingQueueItemViewModel
					{
						CheckingQueueItem = c,
						ContextSlideId = slide.Id,
						ContextSlideTitle = slide.Title,
						ContextMaxScore = (slide as ExerciseSlide)?.Scoring.ScoreWithCodeReview ?? slide.MaxScore,
						ContextTimestamp = c.Timestamp,
						ContextReviews = alreadyChecked ? reviews.GetOrDefault(c.Id, new List<ExerciseCodeReview>()) : new List<ExerciseCodeReview>(),
						ContextExerciseSolution = alreadyChecked && c is ManualExerciseChecking checking ?
							solutions.GetOrDefault(checking.SubmissionId, "") :
							"",
					};
				}).ToList(),
				Groups = groups,
				SelectedGroupsIds = groupsIds,
				Message = message,
				AlreadyChecked = alreadyChecked,
				ExistsMore = checkings.Count > maxShownQueueSize,
				ShowFilterForm = string.IsNullOrEmpty(userId),
				Slides = allCheckingsSlides,
			});
		}

		// Возвращает слайды, по которым есть работы (проверенные или непроверенные, зависит от галочки), разделитель и оставшиеся слайды (не важно проверенные или нет).
		private List<KeyValuePair<Guid, Slide>> GetAllCheckingsSlides(Course course, List<string> groupsIds, ManualCheckingQueueFilterOptions filterOptions)
		{
			filterOptions.SlidesIds = null;
			var usedSlidesIds = GetMergedCheckingQueueSlideIds(filterOptions);

			filterOptions = GetManualCheckingFilterOptionsByGroup(course.Id, groupsIds);
			filterOptions.OnlyChecked = null;
			var allCheckingsSlidesIds = GetMergedCheckingQueueSlideIds(filterOptions);
			var slideId2Index = course.GetSlides(true).Select((s, i) => (s.Id, i))
				.ToDictionary(p => p.Item1, p => p.Item2);

			var emptySlideMock = new Slide { Info = new SlideInfo(null, null), Title = "", Id = Guid.Empty };
			var allCheckingsSlides = allCheckingsSlidesIds
				.Select(s => new KeyValuePair<Guid, Slide>(s, course.FindSlideById(s, true)))
				.Where(kvp => kvp.Value != null)
				.Union(new List<KeyValuePair<Guid, Slide>>
				{
					/* Divider between used slides and another ones */
					new KeyValuePair<Guid, Slide>(Guid.Empty, emptySlideMock)
				})
				.OrderBy(s => usedSlidesIds.Contains(s.Key) ? 0 : 1)
				.ThenBy(s => slideId2Index.GetOrDefault(s.Value.Id))
				.Select(s => new KeyValuePair<Guid, Slide>(s.Key, s.Value))
				.ToList();

			/* Remove divider iff it is first or last item */
			if (allCheckingsSlides.First().Key == Guid.Empty || allCheckingsSlides.Last().Key == Guid.Empty)
				allCheckingsSlides.RemoveAll(kvp => kvp.Key == Guid.Empty);
			
			return allCheckingsSlides;
		}

		public ActionResult CheckingQueue(string courseId, bool done = false, string userId = "", Guid? slideId = null, string message = "")
		{
			var groupsIds = Request.GetMultipleValuesFromQueryString("group");
			return InternalCheckingQueue(courseId, done, groupsIds, userId, slideId, message);
		}

		private async Task<ActionResult> InternalManualChecking<T>(string courseId, int queueItemId, bool ignoreLock = false, List<string> groupsIds = null, bool recheck = false) where T : AbstractManualSlideChecking
		{
			T checking;
			var joinedGroupsIds = string.Join(",", groupsIds ?? new List<string>());
			using (var transaction = db.Database.BeginTransaction())
			{
				checking = slideCheckingsRepo.FindManualCheckingById<T>(queueItemId);
				if (checking == null)
					return RedirectToAction("CheckingQueue",
						new
						{
							courseId = courseId,
							group = joinedGroupsIds,
							done = recheck,
							message = "already_checked",
						});

				if (!userRolesRepo.HasUserAccessToCourse(User.Identity.GetUserId(), checking.CourseId, CourseRole.Instructor))
					return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

				if (checking.IsChecked && !recheck)
					return RedirectToAction("CheckingQueue",
						new
						{
							courseId = checking.CourseId,
							group = joinedGroupsIds,
							done = recheck,
							message = "already_checked",
						});

				if (!recheck)
					await slideCheckingsRepo.LockManualChecking(checking, User.Identity.GetUserId()).ConfigureAwait(false);
				transaction.Commit();
			}

			return RedirectToRoute("Course.SlideById", new
			{
				checking.CourseId,
				checking.SlideId,
				CheckQueueItemId = checking.Id,
				Group = joinedGroupsIds,
			});
		}

		private async Task<ActionResult> CheckNextManualCheckingForSlide<T>(string courseId, Guid slideId, List<string> groupsIds, int previousCheckingId) where T : AbstractManualSlideChecking
		{
			using (var transaction = db.Database.BeginTransaction())
			{
				var filterOptions = GetManualCheckingFilterOptionsByGroup(courseId, groupsIds);
				if (filterOptions.UserIds == null)
					groupsIds = new List<string> { "all" };
				filterOptions.SlidesIds = new List<Guid> { slideId };
				var checkings = slideCheckingsRepo.GetManualCheckingQueue<T>(filterOptions).ToList();

				/* First of all try to find checking with Id < previousCheckingId (early) */
				var itemToCheck = checkings.FirstOrDefault(c => !c.IsLocked && c.Id < previousCheckingId) ?? checkings.FirstOrDefault(c => !c.IsLocked);
				if (itemToCheck == null)
					return RedirectToAction("CheckingQueue", new { courseId, group = string.Join(",", groupsIds), message = "slide_checked" });

				await slideCheckingsRepo.LockManualChecking(itemToCheck, User.Identity.GetUserId()).ConfigureAwait(false);

				transaction.Commit();

				return await InternalManualChecking<T>(courseId, itemToCheck.Id, ignoreLock: true, groupsIds: groupsIds).ConfigureAwait(false);
			}
		}

		public Task<ActionResult> QuizChecking(string courseId, int id, bool recheck = false)
		{
			var groupsIds = Request.GetMultipleValuesFromQueryString("group");
			return InternalManualChecking<ManualQuizChecking>(courseId, id, ignoreLock: false, groupsIds: groupsIds, recheck: recheck);
		}

		public Task<ActionResult> ExerciseChecking(string courseId, int id, bool recheck = false)
		{
			var groupsIds = Request.GetMultipleValuesFromQueryString("group");
			return InternalManualChecking<ManualExerciseChecking>(courseId, id, ignoreLock: false, groupsIds: groupsIds, recheck: recheck);
		}

		public Task<ActionResult> CheckNextQuizForSlide(string courseId, Guid slideId, int previous)
		{
			var groupsIds = Request.GetMultipleValuesFromQueryString("group");
			return CheckNextManualCheckingForSlide<ManualQuizChecking>(courseId, slideId, groupsIds, previous);
		}

		public Task<ActionResult> CheckNextExerciseForSlide(string courseId, Guid slideId, int previous)
		{
			var groupsIds = Request.GetMultipleValuesFromQueryString("group");
			return CheckNextManualCheckingForSlide<ManualExerciseChecking>(courseId, slideId, groupsIds, previous);
		}
		
		public async Task<ActionResult> GetNextManualCheckingExerciseForSlide(string courseId, Guid slideId, int previous)
		{
			var action = await CheckNextExerciseForSlide(courseId, slideId, previous).ConfigureAwait(false);
			if (!(action is RedirectToRouteResult redirect))
				return action;
			var url = Url.RouteUrl(redirect.RouteName, redirect.RouteValues);
			return Json(new { url });
		}
		
		public async Task<ActionResult> GetNextManualCheckingQuizForSlide(string courseId, Guid slideId, int previous)
		{
			var action = await CheckNextQuizForSlide(courseId, slideId, previous).ConfigureAwait(false);
			if (!(action is RedirectToRouteResult redirect))
				return action;
			var url = Url.RouteUrl(redirect.RouteName, redirect.RouteValues);
			return Json(new { url });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> SaveCommentsPolicy(AdminCommentsViewModel model)
		{
			var courseId = model.CourseId;
			var commentsPolicy = new CommentsPolicy
			{
				CourseId = courseId,
				IsCommentsEnabled = model.IsCommentsEnabled,
				ModerationPolicy = model.ModerationPolicy,
				OnlyInstructorsCanReply = model.OnlyInstructorsCanReply
			};
			await commentsRepo.SaveCommentsPolicy(commentsPolicy);
			return RedirectToAction("Comments", new { courseId });
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		public ActionResult Users(UserSearchQueryModel queryModel)
		{
			var userId = User.Identity.GetUserId();
			var isCourseAdmin = userRolesRepo.HasUserAccessToCourse(userId, queryModel.CourseId, CourseRole.CourseAdmin);
			var canAddInstructors = coursesRepo.HasCourseAccess(userId, queryModel.CourseId, CourseAccessType.AddAndRemoveInstructors);
			if (!isCourseAdmin && !canAddInstructors)
				return HttpNotFound();

			if (string.IsNullOrEmpty(queryModel.CourseId))
				return RedirectToAction("Courses", "Course");
			return View(queryModel);
		}

		[ChildActionOnly]
		public ActionResult UsersPartial(UserSearchQueryModel queryModel)
		{
			var userRolesByEmail = userRolesRepo.IsSystemAdministrator(User.Identity.GetUserId()) ? usersRepo.FilterUsersByEmail(queryModel) : null;
			var userRoles = usersRepo.FilterUsers(queryModel);
			var tempCourses = tempCoursesRepo.GetTempCourses().Select(s => s.CourseId).ToHashSet();
			var courses = courseManager.GetCourses()
				.ToDictionary(c => c.Id, c => (c, tempCourses.Contains(c.Id)));
			var model = GetUserListModel(userRolesByEmail.EmptyIfNull().Concat(userRoles).DistinctBy(r => r.UserId).ToList(),
				courses,
				queryModel.CourseId);

			return PartialView("_UserListPartial", model);
		}

		private UserListModel GetUserListModel(List<UserRolesInfo> userRoles,
			Dictionary<string, (Course, bool)> courses,
			string courseId)
		{
			var rolesForUsers = userRolesRepo.GetRolesByUsers(courseId);
			var currentUserId = User.Identity.GetUserId();
			var isCourseAdmin = userRolesRepo.HasUserAccessToCourse(currentUserId, courseId, CourseRole.CourseAdmin);
			var canAddInstructors = coursesRepo.HasCourseAccess(currentUserId, courseId, CourseAccessType.AddAndRemoveInstructors);
			var model = new UserListModel
			{
				CanToggleRoles = isCourseAdmin || canAddInstructors,
				ShowDangerEntities = false,
				Users = new List<UserModel>(),
				CanViewAndToggleCourseAccesses = isCourseAdmin,
				CanViewAndToogleSystemAccesses = false,
				CanViewProfiles = systemAccessesRepo.HasSystemAccess(currentUserId, SystemAccessType.ViewAllProfiles) || userRolesRepo.IsSystemAdministrator(currentUserId),
			};
			var userIds = new HashSet<string>(userRoles.Select(r => r.UserId));
			var usersCourseAccesses = coursesRepo.GetCourseAccesses(courseId).Where(a => userIds.Contains(a.UserId))
				.GroupBy(a => a.UserId)
				.ToDictionary(g => g.Key, g => g.Select(a => a.AccessType).ToList());

			foreach (var userRolesInfo in userRoles)
			{
				var user = new UserModel(userRolesInfo);

				if (!rolesForUsers.TryGetValue(userRolesInfo.UserId, out List<CourseRole> roles))
					roles = new List<CourseRole>();

				user.CourseRoles = Enum.GetValues(typeof(CourseRole))
					.Cast<CourseRole>()
					.Where(courseRole => courseRole != CourseRole.Student)
					.ToDictionary(
						courseRole => courseRole.ToString(),
						courseRole => (ICoursesRolesListModel)new SingleCourseRolesModel
						{
							HasAccess = roles.Contains(courseRole),
							ToggleUrl = Url.Action("ToggleRole", "Account", new { courseId, userId = user.UserId, role = courseRole }),
							UserName = user.UserVisibleName,
							Role = courseRole,
							CourseTitle = courses.GetOrDefault(courseId).Item1?.Title,
							IsTempCourse = courses.GetOrDefault(courseId).Item2
						});

				var courseAccesses = usersCourseAccesses.ContainsKey(user.UserId) ? usersCourseAccesses[user.UserId] : null;
				user.CourseAccesses[courseId] = Enum.GetValues(typeof(CourseAccessType))
					.Cast<CourseAccessType>()
					.ToDictionary(
						a => a,
						a => new CourseAccessModel
						{
							CourseId = courseId,
							HasAccess = courseAccesses?.Contains(a) ?? false,
							ToggleUrl = Url.Action("ToggleCourseAccess", "Admin", new { courseId = courseId, userId = user.UserId, accessType = a }),
							UserName = user.UserVisibleName,
							AccessType = a,
							CourseTitle = courses.GetOrDefault(courseId).Item1?.Title,
							IsTempCourse = courses.GetOrDefault(courseId).Item2
						}
					);

				model.Users.Add(user);
			}

			model.UsersGroups = groupsRepo.GetUsersGroupsNamesAsStrings(courseId, userIds, currentUserId, actual: true, archived: false);
			model.UsersArchivedGroups = groupsRepo.GetUsersGroupsNamesAsStrings(courseId, userIds, currentUserId, actual: false, archived: true);

			return model;
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public ActionResult Diagnostics(string courseId, Guid? versionId)
		{
			var isTempCourse = tempCoursesRepo.Find(courseId) != null;
			if (versionId == null)
			{
				return View(new DiagnosticsModel
				{
					CourseId = courseId,
					IsTempCourse = isTempCourse
				});
			}

			var versionIdGuid = (Guid)versionId;

			var course = courseManager.GetCourse(courseId);
			var version = courseManager.GetVersion(versionIdGuid);

			var courseDiff = new CourseDiff(course, version);
			var schemaPath = Path.Combine(HttpRuntime.BinDirectory, "schema.xsd");
			var validator = new XmlValidator(schemaPath);
			var warnings = validator.ValidateSlidesFiles(version.GetSlides(true).Select(x => x.Info.SlideFile).ToList());

			return View(new DiagnosticsModel
			{
				CourseId = courseId,
				IsDiagnosticsForVersion = true,
				VersionId = versionIdGuid,
				CourseDiff = courseDiff,
				Warnings = warnings,
				IsTempCourse = isTempCourse
			});
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public ActionResult TempCourseDiagnostics(string courseId)
		{
			var authorId = tempCoursesRepo.Find(courseId).AuthorId;
			var baseCourseId = courseId.Replace($"_{authorId}", ""); // todo добавить поле baseCourseId в сущность tempCourse
			var baseCourseVersion = coursesRepo.GetPublishedCourseVersion(baseCourseId).Id;
			return RedirectToAction("Diagnostics", new {courseId, versionId = baseCourseVersion});
		}

		public static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
		{
			/* Check that one directory is not a parent of another one */
			if (source.FullName.StartsWith(target.FullName) || target.FullName.StartsWith(source.FullName))
				throw new Exception("Can\'t copy files recursively from parent to child directory or from child to parent");

			foreach (var subDirectory in source.GetDirectories())
				CopyFilesRecursively(subDirectory, target.CreateSubdirectory(subDirectory.Name));
			foreach (var file in source.GetFiles())
				file.CopyTo(Path.Combine(target.FullName, file.Name), true);
		}

		private async Task NotifyAboutPublishedCourseVersion(string courseId, Guid versionId, string userId)
		{
			var notification = new PublishedPackageNotification
			{
				CourseVersionId = versionId,
			};
			await notificationsRepo.AddNotification(courseId, notification, userId);
		}

		[HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> PublishVersion(string courseId, Guid versionId)
		{
			log.Info($"Публикую версию курса {courseId}. ID версии: {versionId}");
			var versionFile = courseManager.GetCourseVersionFile(versionId);
			var courseFile = courseManager.GetStagingCourseFile(courseId);
			var oldCourse = courseManager.GetCourse(courseId);

			log.Info($"загружаю {versionId} курса {courseId} в таблицу {nameof(CourseFile)}");
			await coursesRepo.AddCourseFile(courseId, versionId, courseFile.ReadAllContent()).ConfigureAwait(false);

			/* First, try to load course from LRU-cache or zip file */
			log.Info($"Загружаю версию {versionId}");
			var version = courseManager.GetVersion(versionId);

			/* Copy version's zip file to course's zip archive, overwrite if need */
			log.Info($"Копирую архив с версий в архив с курсом: {versionFile.FullName} → {courseFile.FullName}");
			versionFile.CopyTo(courseFile.FullName, true);
			courseManager.EnsureVersionIsExtracted(versionId);

			/* Replace courseId */
			version.Id = courseId;

			/* and move course from version's directory to courses's directory */
			var extractedVersionDirectory = courseManager.GetExtractedVersionDirectory(versionId);
			var extractedCourseDirectory = courseManager.GetExtractedCourseDirectory(courseId);
			log.Info($"Перемещаю папку с версией в папку с курсом: {extractedVersionDirectory.FullName} → {extractedCourseDirectory.FullName}");
			courseManager.MoveCourse(
				version,
				extractedVersionDirectory,
				extractedCourseDirectory);

			log.Info($"Помечаю версию {versionId} как опубликованную версию курса {courseId}");
			await coursesRepo.MarkCourseVersionAsPublished(versionId);
			await NotifyAboutPublishedCourseVersion(courseId, versionId, User.Identity.GetUserId());

			log.Info($"Обновляю курс {courseId} в оперативной памяти");
			courseManager.UpdateCourseVersion(courseId, versionId);
			courseManager.ReloadCourseNotSafe(courseId);

			var courseDiff = new CourseDiff(oldCourse, version);

			return View("Diagnostics", new DiagnosticsModel
			{
				CourseId = courseId,
				IsDiagnosticsForVersion = true,
				IsVersionPublished = true,
				VersionId = versionId,
				CourseDiff = courseDiff,
			});
		}

		[HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> DeleteVersion(string courseId, Guid versionId)
		{
			/* Remove notifications from database */
			await notificationsRepo.RemoveNotifications(versionId);

			/* Remove information from database */
			await coursesRepo.DeleteCourseVersion(courseId, versionId);

			/* Delete zip-archive from file system */
			courseManager.GetCourseVersionFile(versionId).Delete();

			return RedirectToAction("Packages", new { courseId });
		}

		public ActionResult Groups(string courseId)
		{
			/* This action is moved to react-based frontend application */
			return Redirect($"/{courseId.ToLower(CultureInfo.InvariantCulture)}/groups");
		}

		public class UserSearchResultModel
		{
			public string id { get; set; }
			public string value { get; set; }
		}

		public ActionResult FindUsers(string courseId, string term, bool onlyInstructors = true, bool withGroups = true)
		{
			/* Only sysadmins can search ordinary users */
			// This limitation is disabled now for certificates generation. Waits for futher investigation
			// if (!User.IsSystemAdministrator() && !onlyInstructors)
			//   	return Json(new { status = "error", message = "Вы не можете искать среди всех пользователей" }, JsonRequestBehavior.AllowGet);

			var query = new UserSearchQueryModel { NamePrefix = term };
			if (onlyInstructors)
			{
				query.CourseRole = CourseRole.Instructor;
				query.CourseId = courseId;
				query.IncludeHighCourseRoles = true;
			}

			var users = usersRepo.FilterUsers(query, 10).ToList();
			var usersList = users.Select(ur => new UserSearchResultModel
			{
				id = ur.UserId,
				value = $"{ur.UserVisibleName} ({ur.UserName})"
			}).ToList();

			if (withGroups)
			{
				var usersIds = users.Select(u => u.UserId);
				var groupsNames = groupsRepo.GetUsersGroupsNamesAsStrings(courseId, usersIds, User.Identity.GetUserId(), actual: true, archived: false);
				foreach (var user in usersList)
					if (groupsNames.ContainsKey(user.id) && !string.IsNullOrEmpty(groupsNames[user.id]))
						user.value += $": {groupsNames[user.id]}";
			}

			return Json(usersList, JsonRequestBehavior.AllowGet);
		}

		public ActionResult Certificates(string courseId)
		{
			var course = courseManager.GetCourse(courseId);
			var certificateTemplates = certificatesRepo.GetTemplates(courseId).ToDictionary(t => t.Id);
			var certificates = certificatesRepo.GetCertificates(courseId);
			var templateParameters = certificateTemplates.ToDictionary(
				kv => kv.Key,
				kv => certificateGenerator.GetTemplateParametersWithoutBuiltins(kv.Value).ToList()
			);

			return View(new CertificatesViewModel
			{
				Course = course,
				Templates = certificateTemplates,
				TemplateParameters = templateParameters,
				Certificates = certificates
			});
		}

		[HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> CreateCertificateTemplate(string courseId, string name, HttpPostedFileBase archive)
		{
			if (archive == null || archive.ContentLength <= 0)
			{
				log.Error("Создание шаблона сертификата: ошибка загрузки архива");
				throw new Exception("Ошибка загрузки архива");
			}

			log.Info($"Создаю шаблон сертификата «{name}» для курса {courseId}");
			var archiveName = SaveUploadedTemplate(archive);
			var template = await certificatesRepo.AddTemplate(courseId, name, archiveName);
			await LoadUploadedTemplateToBD(archiveName, template.Id).ConfigureAwait(false);
			log.Info($"Создал шаблон, Id = {template.Id}, путь к архиву {template.ArchiveName}");
			return RedirectToAction("Certificates", new { courseId });
		}

		private string SaveUploadedTemplate(HttpPostedFileBase archive)
		{
			var archiveName = Utils.NewNormalizedGuid();
			var templateArchivePath = certificateGenerator.GetTemplateArchivePath(archiveName);
			try
			{
				archive.SaveAs(templateArchivePath.FullName);
			}
			catch (Exception e)
			{
				log.Error(e, "Создание шаблона сертификата: не могу сохранить архив");
				throw;
			}

			return archiveName;
		}

		private async Task LoadUploadedTemplateToBD(string archiveName, Guid templateId)
		{
			var content = await certificateGenerator.GetTemplateArchivePath(archiveName).ReadAllContentAsync().ConfigureAwait(false);
			await certificatesRepo.AddCertificateTemplateArchive(archiveName, templateId, content).ConfigureAwait(false);
		}

		[HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> EditCertificateTemplate(string courseId, Guid templateId, string name, HttpPostedFileBase archive)
		{
			var template = certificatesRepo.FindTemplateById(templateId);
			if (template == null || !template.CourseId.EqualsIgnoreCase(courseId))
				return HttpNotFound();

			log.Info($"Обновляю шаблон сертификата «{template.Name}» (Id = {template.Id}) для курса {courseId}");

			if (archive != null && archive.ContentLength > 0)
			{
				var archiveName = SaveUploadedTemplate(archive);
				await LoadUploadedTemplateToBD(archiveName, template.Id).ConfigureAwait(false);
				log.Info($"Загружен новый архив в {archiveName}");
				await certificatesRepo.ChangeTemplateArchiveName(templateId, archiveName);
			}

			await certificatesRepo.ChangeTemplateName(templateId, name);

			return RedirectToAction("Certificates", new { courseId });
		}

		[HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public async Task<ActionResult> RemoveCertificateTemplate(string courseId, Guid templateId)
		{
			var template = certificatesRepo.FindTemplateById(templateId);
			if (template == null || !template.CourseId.EqualsIgnoreCase(courseId))
				return HttpNotFound();

			log.Info($"Удаляю шаблон сертификата «{template.Name}» (Id = {template.Id}) для курса {courseId}");
			await certificatesRepo.RemoveTemplate(template);

			return RedirectToAction("Certificates", new { courseId });
		}

		private async Task NotifyAboutCertificate(Certificate certificate)
		{
			var notification = new ReceivedCertificateNotification
			{
				Certificate = certificate,
			};
			var ulearnBotUserId = usersRepo.GetUlearnBotUserId();
			await notificationsRepo.AddNotification(certificate.Template.CourseId, notification, ulearnBotUserId);
		}

		[HttpPost]
		[ValidateInput(false)]
		public async Task<ActionResult> AddCertificate(string courseId, Guid templateId, string userId, bool isPreview = false)
		{
			var template = certificatesRepo.FindTemplateById(templateId);
			if (template == null || !template.CourseId.EqualsIgnoreCase(courseId))
				return HttpNotFound();

			var certificateParameters = GetCertificateParametersFromRequest(template);
			if (certificateParameters == null)
				return RedirectToAction("Certificates", new { courseId });

			var certificate = await certificatesRepo.AddCertificate(templateId, userId, User.Identity.GetUserId(), certificateParameters, isPreview);

			if (isPreview)
				return RedirectToRoute("Certificate", new { certificateId = certificate.Id });

			await NotifyAboutCertificate(certificate);

			return Redirect(Url.Action("Certificates", new { courseId }) + "#template-" + templateId);
		}

		private Dictionary<string, string> GetCertificateParametersFromRequest(CertificateTemplate template)
		{
			var templateParameters = certificateGenerator.GetTemplateParametersWithoutBuiltins(template);
			var certificateParameters = new Dictionary<string, string>();
			foreach (var parameter in templateParameters)
			{
				if (Request.Unvalidated.Form["parameter-" + parameter] == null)
					return null;
				certificateParameters[parameter] = Request.Unvalidated.Form["parameter-" + parameter];
			}

			return certificateParameters;
		}

		[HttpPost]
		public async Task<ActionResult> RemoveCertificate(string courseId, Guid certificateId)
		{
			var certificate = certificatesRepo.FindCertificateById(certificateId);
			if (certificate == null)
				return HttpNotFound();

			if (!userRolesRepo.HasUserAccessToCourse(User.Identity.GetUserId(), certificate.Template.CourseId, CourseRole.CourseAdmin) &&
				certificate.InstructorId != User.Identity.GetUserId())
				return HttpNotFound();

			await certificatesRepo.RemoveCertificate(certificate);
			return Json(new { status = "ok" });
		}

		public ActionResult DownloadCertificateTemplate(string courseId, Guid templateId)
		{
			var template = certificatesRepo.FindTemplateById(templateId);
			if (template == null || !template.CourseId.EqualsIgnoreCase(courseId))
				return HttpNotFound();

			return RedirectPermanent($"/Certificates/{template.ArchiveName}.zip");
		}

		public ActionResult PreviewCertificates(string courseId, Guid templateId, HttpPostedFileBase certificatesData)
		{
			const string namesColumnName = "Фамилия Имя";

			var template = certificatesRepo.FindTemplateById(templateId);
			if (template == null || !template.CourseId.EqualsIgnoreCase(courseId))
				return HttpNotFound();

			var notBuiltinTemplateParameters = certificateGenerator.GetTemplateParametersWithoutBuiltins(template).ToList();
			var builtinTemplateParameters = certificateGenerator.GetBuiltinTemplateParameters(template).ToList();
			builtinTemplateParameters.Sort();

			var model = new PreviewCertificatesViewModel
			{
				CourseId = courseId,
				Template = template,
				NotBuiltinTemplateParameters = notBuiltinTemplateParameters,
				BuiltinTemplateParameters = builtinTemplateParameters,
				Certificates = new List<PreviewCertificatesCertificateModel>(),
			};

			if (certificatesData == null || certificatesData.ContentLength <= 0)
			{
				return View(model.WithError("Ошибка загрузки файла с данными для сертификатов"));
			}

			var allUsersIds = new HashSet<string>();

			using (var parser = new TextFieldParser(new StreamReader(certificatesData.InputStream)))
			{
				parser.TextFieldType = FieldType.Delimited;
				parser.SetDelimiters(",");
				if (parser.EndOfData)
					return View(model.WithError("Пустой файл? В файле с данными должна присутствовать строка с заголовком"));

				string[] headers;
				try
				{
					headers = parser.ReadFields();
				}
				catch (MalformedLineException e)
				{
					return View(model.WithError($"Ошибка в файле с данными: в строке {parser.ErrorLineNumber}: \"{parser.ErrorLine}\". {e}"));
				}

				var namesColumnIndex = headers.FindIndex(namesColumnName);
				if (namesColumnIndex < 0)
					return View(model.WithError($"Одно из полей должно иметь имя \"{namesColumnName}\", в нём должны содержаться фамилия и имя студента. Смотрите пример файла с данными"));

				var parametersIndeces = new Dictionary<string, int>();
				foreach (var parameter in notBuiltinTemplateParameters)
				{
					parametersIndeces[parameter] = headers.FindIndex(parameter);
					if (parametersIndeces[parameter] < 0)
						return View(model.WithError($"Одно из полей должно иметь имя \"{parameter}\", в нём должна содержаться подстановка для шаблона. Смотрите пример файла с данными"));
				}

				while (!parser.EndOfData)
				{
					string[] fields;
					try
					{
						fields = parser.ReadFields();
					}
					catch (MalformedLineException e)
					{
						return View(model.WithError($"Ошибка в файле с данными: в строке {parser.ErrorLineNumber}: \"{parser.ErrorLine}\". {e}"));
					}

					if (fields.Length != headers.Length)
					{
						return View(model.WithError($"Ошибка в файле с данными: в строке {parser.ErrorLineNumber}: \"{parser.ErrorLine}\". Количество ячеек в строке не совпадает с количеством столбцов в заголовке"));
					}

					var userNames = fields[namesColumnIndex];

					var query = new UserSearchQueryModel { NamePrefix = userNames };
					var users = usersRepo.FilterUsers(query).Take(10).ToList();

					var exportedParameters = parametersIndeces.ToDictionary(kv => kv.Key, kv => fields[kv.Value]);
					var certificateModel = new PreviewCertificatesCertificateModel
					{
						UserNames = userNames,
						Users = users,
						Parameters = exportedParameters,
					};
					allUsersIds.AddAll(users.Select(u => u.UserId));
					model.Certificates.Add(certificateModel);
				}
			}

			model.GroupsNames = groupsRepo.GetUsersGroupsNamesAsStrings(courseId, allUsersIds, User.Identity.GetUserId(), actual: true, archived: false);

			return View(model);
		}

		public async Task<ActionResult> GenerateCertificates(string courseId, Guid templateId, int maxCertificateId)
		{
			var template = certificatesRepo.FindTemplateById(templateId);
			if (template == null || !template.CourseId.EqualsIgnoreCase(courseId))
				return HttpNotFound();

			var templateParameters = certificateGenerator.GetTemplateParametersWithoutBuiltins(template).ToList();
			var certificateRequests = new List<CertificateRequest>();

			for (var certificateIndex = 0; certificateIndex < maxCertificateId; certificateIndex++)
			{
				var userId = Request.Form.Get($"user-{certificateIndex}");
				if (userId == null)
					continue;
				if (string.IsNullOrEmpty(userId))
				{
					return View("GenerateCertificatesError", (object)"Не все пользователи выбраны");
				}

				var parameters = new Dictionary<string, string>();
				foreach (var parameterName in templateParameters)
				{
					var parameterValue = Request.Unvalidated.Form.Get($"parameter-{certificateIndex}-{parameterName}");
					parameters[parameterName] = parameterValue;
				}

				certificateRequests.Add(new CertificateRequest
				{
					UserId = userId,
					Parameters = parameters,
				});
			}

			foreach (var certificateRequest in certificateRequests)
			{
				var certificate = await certificatesRepo.AddCertificate(templateId, certificateRequest.UserId, User.Identity.GetUserId(), certificateRequest.Parameters);
				await NotifyAboutCertificate(certificate);
			}

			return Redirect(Url.Action("Certificates", new { courseId }) + "#template-" + templateId);
		}

		public async Task<ActionResult> GetBuiltinCertificateParametersForUser(string courseId, Guid templateId, string userId)
		{
			var template = certificatesRepo.FindTemplateById(templateId);
			if (template == null || !template.CourseId.EqualsIgnoreCase(courseId))
				return HttpNotFound();

			var user = await userManager.FindByIdAsync(userId);
			if (user == null)
				return HttpNotFound();
			var instructor = await userManager.FindByIdAsync(User.Identity.GetUserId());
			var course = courseManager.GetCourse(courseId);

			var builtinParameters = certificateGenerator.GetBuiltinTemplateParameters(template);
			var builtinParametersValues = builtinParameters.ToDictionary(
				p => p,
				p => certificateGenerator.GetTemplateBuiltinParameterForUser(template, course, user, instructor, p)
			);

			return Json(builtinParametersValues, JsonRequestBehavior.AllowGet);
		}

		private async Task NotifyAboutAdditionalScore(AdditionalScore score)
		{
			var notification = new ReceivedAdditionalScoreNotification
			{
				Score = score
			};
			await notificationsRepo.AddNotification(score.CourseId, notification, score.InstructorId);
		}

		[HttpPost]
		public async Task<ActionResult> SetAdditionalScore(string courseId, Guid unitId, string userId, string scoringGroupId, string score)
		{
			var course = courseManager.GetCourse(courseId);
			if (!course.Settings.Scoring.Groups.ContainsKey(scoringGroupId))
				return HttpNotFound();
			var unit = course.GetUnitsNotSafe().FirstOrDefault(u => u.Id == unitId);
			if (unit == null)
				return HttpNotFound();

			var scoringGroup = unit.Scoring.Groups[scoringGroupId];
			if (string.IsNullOrEmpty(score))
			{
				await additionalScoresRepo.RemoveAdditionalScores(courseId, unitId, userId, scoringGroupId).ConfigureAwait(false);
				return Json(new { status = "ok", score = "" });
			}

			if (!int.TryParse(score, out int scoreInt))
				return Json(new { status = "error", error = "Введите целое число" });
			if (scoreInt < 0 || scoreInt > scoringGroup.MaxAdditionalScore)
				return Json(new { status = "error", error = $"Баллы должны быть от 0 до {scoringGroup.MaxAdditionalScore}" });

			var (additionalScore, oldScore) =
				await additionalScoresRepo.SetAdditionalScore(courseId, unitId, userId, scoringGroupId, scoreInt, User.Identity.GetUserId()).ConfigureAwait(false);
			if (!oldScore.HasValue || oldScore.Value != scoreInt)
				await NotifyAboutAdditionalScore(additionalScore).ConfigureAwait(false);

			return Json(new { status = "ok", score = scoreInt });
		}

		[HttpPost]
		public async Task<ActionResult> AddLabelToGroup(int groupId, int labelId)
		{
			var label = groupsRepo.FindLabelById(labelId);
			if (label == null || label.OwnerId != User.Identity.GetUserId())
				return Json(new { status = "error", message = "Label not found or not owned by you" });

			await groupsRepo.AddLabelToGroup(groupId, labelId).ConfigureAwait(false);
			return Json(new { status = "ok" });
		}

		[HttpPost]
		public async Task<ActionResult> RemoveLabelFromGroup(int groupId, int labelId)
		{
			var label = groupsRepo.FindLabelById(labelId);
			if (label == null || label.OwnerId != User.Identity.GetUserId())
				return Json(new { status = "error", message = "Label not found or not owned by you" });

			await groupsRepo.RemoveLabelFromGroup(groupId, labelId);
			return Json(new { status = "ok" });
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		[HttpPost]
		public async Task<ActionResult> ToggleCourseAccess(string courseId, string userId, CourseAccessType accessType, bool isEnabled)
		{
			var currentUserId = User.Identity.GetUserId();
			var comment = Request.Form["comment"];
			var userRoles = userRolesRepo.GetRoles(userId);
			var errorMessage = "Выдавать дополнительные права можно только преподавателям. Сначала назначьте пользователя администратором курса или преподавателем";
			if (!userRoles.ContainsKey(courseId))
				return Json(new { status = "error", message = errorMessage });
			if (userRoles[courseId] > CourseRole.Instructor)
				return Json(new { status = "error", message = errorMessage });

			if (isEnabled)
				await coursesRepo.GrantAccess(courseId, userId, accessType, currentUserId, comment);
			else
				await coursesRepo.RevokeAccess(courseId, userId, accessType, currentUserId, comment);

			return Json(new { status = "ok" });
		}

		[SysAdminsOnly]
		public async Task<ActionResult> StyleValidations()
		{
			return View(await styleErrorsRepo.GetStyleErrorSettingsAsync());
		}

		[SysAdminsOnly]
		[HttpPost]
		public async Task<ActionResult> EnableStyleValidation(StyleErrorType errorType, bool isEnabled)
		{
			await styleErrorsRepo.EnableStyleErrorAsync(errorType, isEnabled);
			return Json(new { status = "ok" });
		}

		[SysAdminsOnly]
		[HttpPost]
		public async Task<ActionResult> UploadPublishedCoursesToBd()
		{
			var courses = courseManager.GetCourses();
			foreach (var course in courses)
			{
				var publishedVersion = coursesRepo.GetPublishedCourseVersion(course.Id);
				if (publishedVersion == null)
					continue;
				var fileInfo = courseManager.GetStagingCourseFile(course.Id);
				var content = await fileInfo.ReadAllContentAsync().ConfigureAwait(false);
				await coursesRepo.AddCourseFile(course.Id, publishedVersion.Id, content).ConfigureAwait(false);
			}

			return Json(new { status = "ok" });
		}

		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		[HttpPost]
		public async Task<ActionResult> SetSuspicionLevels(string courseId, Guid slideId, string faintSuspicion = null, string strongSuspicion = null)
		{
			var course = courseManager.GetCourse(courseId);
			if (course.GetSlides(true).All(s => s.Id != slideId))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden, "Course does not contain a slide");

			if (!TryParseNullableDouble(faintSuspicion, out var faintSuspicionParsed)
				|| !TryParseNullableDouble(strongSuspicion, out var strongSuspicionParsed))
				return new HttpStatusCodeResult(HttpStatusCode.BadRequest, "faintSuspicion or strongSuspicion not in double");

			if (faintSuspicion != null && (faintSuspicionParsed < 0 || faintSuspicionParsed > 100)
				|| strongSuspicion != null && (strongSuspicionParsed < 0 || strongSuspicionParsed > 100)
				|| faintSuspicionParsed > strongSuspicionParsed)
				return new HttpStatusCodeResult(HttpStatusCode.BadRequest, "faintSuspicion < strongSuspicion and in [0, 100]");

			await antiPlagiarismClient.SetSuspicionLevelsAsync(new SetSuspicionLevelsParameters
			{
				TaskId = slideId,
				FaintSuspicion = faintSuspicionParsed / 100d,
				StrongSuspicion = strongSuspicionParsed / 100d,
			});
			return Json(new { status = "ok" });
		}

		public static bool TryParseNullableDouble(string str, out double? result)
		{
			result = null;
			if (string.IsNullOrWhiteSpace(str))
				return true;
			str = str.Replace(',', '.');
			if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
			{
				result = d;
				return true;
			}
			return false;
		}
	}

	public class CertificateRequest
	{
		public string UserId;
		public Dictionary<string, string> Parameters;
	}

	public class PreviewCertificatesCertificateModel
	{
		public string UserNames { get; set; }
		public List<UserRolesInfo> Users { get; set; }
		public Dictionary<string, string> Parameters { get; set; }
	}

	public class PreviewCertificatesViewModel
	{
		public string Error { get; set; }

		public string CourseId { get; set; }
		public CertificateTemplate Template { get; set; }
		public List<PreviewCertificatesCertificateModel> Certificates { get; set; }
		public List<string> NotBuiltinTemplateParameters { get; set; }
		public List<string> BuiltinTemplateParameters { get; set; }
		public Dictionary<string, string> GroupsNames { get; set; }

		public PreviewCertificatesViewModel WithError(string error)
		{
			return new PreviewCertificatesViewModel
			{
				CourseId = CourseId,
				Template = Template,
				Error = error,
			};
		}
	}

	public class CertificatesViewModel
	{
		public Course Course { get; set; }

		public Dictionary<Guid, CertificateTemplate> Templates { get; set; }
		public Dictionary<Guid, List<Certificate>> Certificates { get; set; }
		public Dictionary<Guid, List<string>> TemplateParameters { get; set; }
	}

	public class UnitsListViewModel
	{
		public string CourseId;
		public string CourseTitle;
		public DateTime CurrentDateTime;
		public List<Tuple<Unit, UnitAppearance>> Units;

		public UnitsListViewModel(string courseId, string courseTitle, List<Tuple<Unit, UnitAppearance>> units,
			DateTime currentDateTime)
		{
			CourseId = courseId;
			CourseTitle = courseTitle;
			Units = units;
			CurrentDateTime = currentDateTime;
		}
	}

	public class CourseListViewModel
	{
		public List<CourseViewModel> Courses;
		public string LastTryCourseId { get; set; }
		public string LastTryCourseTitle { get; set; }
		public string InvalidCharacters { get; set; }
	}

	public class CourseViewModel
	{
		public string Title { get; set; }
		public string Id { get; set; }
		public DateTime LastWriteTime { get; set; }
		public bool IsTemp { get; set; }
	}

	public class PackagesViewModel
	{
		public Course Course { get; set; }
		public bool HasPackage { get; set; }
		public DateTime LastUpdate { get; set; }
		public List<CourseVersion> Versions { get; set; }
		public CourseVersion PublishedVersion { get; set; }
		public CourseGit CourseGit { get; set; }
		public bool OpenStep1 { get; set; }
		public bool OpenStep2 { get; set; }
		public string GitSecret { get; set; }

		public string Error { get; set; }
		public string HelpUrl { get; set; } = "https://docs.google.com/document/d/1tL_D2SGIv163GpVVr5HrZTBEgcMk5shCKN5J6le4pTc/edit?usp=sharing";
	}

	public class AdminCommentsViewModel
	{
		public string CourseId { get; set; }
		public bool IsCommentsEnabled { get; set; }
		public CommentModerationPolicy ModerationPolicy { get; set; }
		public bool OnlyInstructorsCanReply { get; set; }
		public List<CommentViewModel> Comments { get; set; }
	}

	public class ManualCheckingQueueViewModel
	{
		public string CourseId { get; set; }
		public List<ManualCheckingQueueItemViewModel> Checkings { get; set; }
		public string Message { get; set; }
		public List<Group> Groups { get; set; }
		public List<string> SelectedGroupsIds { get; set; }
		public string SelectedGroupsIdsJoined => string.Join(",", SelectedGroupsIds);
		public bool AlreadyChecked { get; set; }
		public bool ExistsMore { get; set; }
		public bool ShowFilterForm { get; set; }
		public List<KeyValuePair<Guid, Slide>> Slides { get; set; }
	}

	public class ManualCheckingQueueItemViewModel
	{
		public AbstractManualSlideChecking CheckingQueueItem { get; set; }

		public Guid ContextSlideId { get; set; }
		public string ContextSlideTitle { get; set; }
		public int ContextMaxScore { get; set; }
		public List<ExerciseCodeReview> ContextReviews { get; set; }
		public string ContextExerciseSolution { get; set; }
		public DateTime ContextTimestamp { get; set; }
	}

	public class DiagnosticsModel
	{
		public string CourseId { get; set; }
		public bool IsDiagnosticsForVersion { get; set; }
		public bool IsVersionPublished { get; set; }
		public Guid VersionId { get; set; }
		public CourseDiff CourseDiff { get; set; }
		public string Warnings { get; set; }
		public bool IsTempCourse { get; set; }
	}
}