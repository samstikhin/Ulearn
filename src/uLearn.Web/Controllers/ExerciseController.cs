using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Mvc;
using Database;
using Database.DataContexts;
using Database.Extensions;
using Database.Models;
using JetBrains.Annotations;
using Microsoft.AspNet.Identity;
using uLearn.Web.Extensions;
using uLearn.Web.FilterAttributes;
using uLearn.Web.Models;
using Ulearn.Common;
using Ulearn.Common.Extensions;
using Ulearn.Core.Courses;
using Ulearn.Core.Courses.Slides;
using Ulearn.Core.Courses.Slides.Exercises;
using Ulearn.Core.Courses.Slides.Exercises.Blocks;
using Ulearn.Core.Helpers;

namespace uLearn.Web.Controllers
{
	[ULearnAuthorize]
	public class ExerciseController : BaseExerciseController
	{
		private readonly ExerciseStudentZipsCache exerciseStudentZipsCache;
		private readonly UserRolesRepo userRolesRepo;

		public ExerciseController()
		{
			exerciseStudentZipsCache = new ExerciseStudentZipsCache();
			userRolesRepo = new UserRolesRepo(db);
		}

		[System.Web.Mvc.HttpPost]
		public async Task<ActionResult> RunSolution(string courseId, Guid slideId, bool isLti = false)
		{
			return HttpNotFound();
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		[System.Web.Mvc.HttpPost]
		[ValidateInput(false)]
		public async Task<ActionResult> AddExerciseCodeReview(string courseId, int checkingId, [FromBody] ReviewInfo reviewInfo)
		{
			var checking = slideCheckingsRepo.FindManualCheckingById<ManualExerciseChecking>(checkingId);
			if (!string.Equals(checking.CourseId, courseId, StringComparison.OrdinalIgnoreCase))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			/* Make start position less than finish position */
			if (reviewInfo.StartLine > reviewInfo.FinishLine || (reviewInfo.StartLine == reviewInfo.FinishLine && reviewInfo.StartPosition > reviewInfo.FinishPosition))
			{
				var tmp = reviewInfo.StartLine;
				reviewInfo.StartLine = reviewInfo.FinishLine;
				reviewInfo.FinishLine = tmp;

				tmp = reviewInfo.StartPosition;
				reviewInfo.StartPosition = reviewInfo.FinishPosition;
				reviewInfo.FinishPosition = tmp;
			}

			var review = await slideCheckingsRepo.AddExerciseCodeReview(checking, User.Identity.GetUserId(), reviewInfo.StartLine, reviewInfo.StartPosition, reviewInfo.FinishLine, reviewInfo.FinishPosition, reviewInfo.Comment).ConfigureAwait(false);
			var currentUser = usersRepo.FindUserById(User.Identity.GetUserId());

			return PartialView("_ExerciseReview", new ExerciseCodeReviewModel
			{
				Review = review,
				ManualChecking = checking,
				CurrentUser = currentUser,
				CanReply = true,
			});
		}

		[System.Web.Mvc.HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		public async Task<ActionResult> DeleteExerciseCodeReview(string courseId, int reviewId)
		{
			var userId = User.Identity.GetUserId();
			var review = slideCheckingsRepo.FindExerciseCodeReviewById(reviewId);
			var reviewCourseId = review.ExerciseCheckingId.HasValue ? review.ExerciseChecking.CourseId : review.Submission.CourseId;
			if (!reviewCourseId.EqualsIgnoreCase(courseId))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
			if (review.AuthorId != User.Identity.GetUserId() && !userRolesRepo.HasUserAccessToCourse(userId, courseId, CourseRole.CourseAdmin))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			await slideCheckingsRepo.DeleteExerciseCodeReview(review).ConfigureAwait(false);

			return Json(new CodeReviewOperationResult { Status = "ok" });
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		[System.Web.Mvc.HttpPost]
		[ValidateInput(false)]
		public async Task<ActionResult> UpdateExerciseCodeReview(string courseId, int reviewId, string comment)
		{
			var review = slideCheckingsRepo.FindExerciseCodeReviewById(reviewId);
			if (!review.ExerciseChecking.CourseId.EqualsIgnoreCase(courseId))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
			if (review.AuthorId != User.Identity.GetUserId())
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			await slideCheckingsRepo.UpdateExerciseCodeReview(review, comment).ConfigureAwait(false);

			return Json(new CodeReviewOperationResult { Status = "ok" });
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		[System.Web.Mvc.HttpPost]
		[ValidateInput(false)]
		public async Task<ActionResult> HideFromTopCodeReviewComments(string courseId, Guid slideId, string comment)
		{
			var slide = courseManager.FindCourse(courseId)?.FindSlideById(slideId, true) as ExerciseSlide;
			if (slide == null)
				return HttpNotFound();

			var userId = User.Identity.GetUserId();
			await slideCheckingsRepo.HideFromTopCodeReviewComments(courseId, slideId, userId, comment).ConfigureAwait(false);

			var topUserReviewComments = slideCheckingsRepo.GetTopUserReviewComments(courseId, slideId, userId, 20);
			var topOtherUsersReviewComments = slideCheckingsRepo.GetTopOtherUsersReviewComments(courseId, slideId, userId, 10, topUserReviewComments);
			return PartialView("_TopUserReviewComments", new ExerciseBlockData(courseId, slide, false)
			{
				TopUserReviewComments = topUserReviewComments,
				TopOtherUsersReviewComments = topOtherUsersReviewComments,
			});
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.CourseAdmin)]
		public ActionResult SlideCodeReviewComments(string courseId, Guid slideId)
		{
			var comments = slideCheckingsRepo.GetLastYearReviewComments(courseId, slideId);
			return PartialView("_SlideCodeReviewComments", comments);
		}

		[System.Web.Mvc.HttpPost]
		[ValidateInput(false)]
		public async Task<ActionResult> AddExerciseCodeReviewComment(int reviewId, string text)
		{
			var userId = User.Identity.GetUserId();
			var review = slideCheckingsRepo.FindExerciseCodeReviewById(reviewId);
			var currentUserId = User.Identity.GetUserId();

			var submissionUserId = review.ExerciseCheckingId.HasValue ? review.ExerciseChecking.UserId : review.Submission.UserId;
			var submissionCourseId = review.ExerciseCheckingId.HasValue ? review.ExerciseChecking.CourseId : review.Submission.CourseId;
			var isInstructor = User.HasAccessFor(submissionCourseId, CourseRole.Instructor);
			if (submissionUserId != currentUserId && !isInstructor)
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			var canReply = isInstructor || !review.Author.IsUlearnBot() || review.NotDeletedComments.Any(c => !c.Author.IsUlearnBot());
			if (!canReply)
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			var comment = await slideCheckingsRepo.AddExerciseCodeReviewComment(currentUserId, reviewId, text).ConfigureAwait(false);

			if (review.ExerciseCheckingId.HasValue && review.ExerciseChecking.IsChecked)
			{
				var course = courseManager.FindCourse(submissionCourseId);
				var slideId = review.ExerciseChecking.SlideId;
				var unit = course?.FindUnitBySlideId(slideId, isInstructor);
				if (unit != null && unitsRepo.IsUnitVisibleForStudents(course, unit.Id))
					await NotifyAboutCodeReviewComment(comment).ConfigureAwait(false);
			}

			return PartialView("_ExerciseReviewComment", comment);
		}

		[System.Web.Mvc.HttpPost]
		public async Task<ActionResult> DeleteExerciseCodeReviewComment(int commentId)
		{
			var comment = slideCheckingsRepo.FindExerciseCodeReviewCommentById(commentId);
			if (comment == null)
				return HttpNotFound();

			var currentUserId = User.Identity.GetUserId();
			var courseId = comment.Review.ExerciseCheckingId.HasValue ? comment.Review.ExerciseChecking.CourseId : comment.Review.Submission.CourseId;
			if (comment.AuthorId != currentUserId && !User.HasAccessFor(courseId, CourseRole.CourseAdmin))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			await slideCheckingsRepo.DeleteExerciseCodeReviewComment(comment).ConfigureAwait(false);

			return Json(new CodeReviewOperationResult { Status = "ok" });
		}


		/* Call NotifyAboutCodeReviewComment() only for checking's comment, not for submission's ones */
		private async Task NotifyAboutCodeReviewComment(ExerciseCodeReviewComment comment)
		{
			var courseId = comment.Review.ExerciseCheckingId.HasValue ? comment.Review.ExerciseChecking.CourseId : comment.Review.Submission.CourseId;
			await notificationsRepo.AddNotification(courseId, new ReceivedCommentToCodeReviewNotification
			{
				CommentId = comment.Id,
			}, comment.AuthorId).ConfigureAwait(false);
		}

		[System.Web.Mvc.HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		public async Task<ActionResult> ScoreExercise(int id, string nextUrl, string exercisePercent, bool prohibitFurtherReview, string errorUrl = "", bool recheck = false)
		{
			if (string.IsNullOrEmpty(errorUrl))
				errorUrl = nextUrl;

			using (var transaction = db.Database.BeginTransaction())
			{
				var checking = slideCheckingsRepo.FindManualCheckingById<ManualExerciseChecking>(id);

				var course = courseManager.GetCourse(checking.CourseId);
				var slide = (ExerciseSlide)course.GetSlideById(checking.SlideId, true);

				/* Invalid form: percent isn't integer */
				if (!int.TryParse(exercisePercent, out var percent))
					return Json(new ScoreExerciseOperationResult { Status = "error", Redirect = errorUrl + "Неверное количество процентов"});

				/* Invalid form: score isn't from range 0..100 */
				if (percent < 0 || percent > 100)
					return Json(new ScoreExerciseOperationResult { Status = "error", Redirect = errorUrl + $"Неверное количество процентов: {percent}"});

				checking.ProhibitFurtherManualCheckings = prohibitFurtherReview;
				await slideCheckingsRepo.MarkManualExerciseCheckingAsChecked(checking, percent).ConfigureAwait(false);
				await slideCheckingsRepo.MarkManualExerciseCheckingAsCheckedBeforeThis(checking).ConfigureAwait(false);
				if (prohibitFurtherReview)
					await slideCheckingsRepo.ProhibitFurtherExerciseManualChecking(checking.CourseId, checking.UserId, checking.SlideId).ConfigureAwait(false);
				else
					await slideCheckingsRepo.DisableProhibitFurtherManualCheckings(checking.CourseId, checking.UserId, checking.SlideId).ConfigureAwait(false);
				await visitsRepo.UpdateScoreForVisit(checking.CourseId, slide, checking.UserId).ConfigureAwait(false);

				transaction.Commit();

				var unit = course.FindUnitBySlideId(checking.SlideId, true);
				if (unit != null && unitsRepo.IsUnitVisibleForStudents(course, unit.Id))
					await NotifyAboutManualExerciseChecking(checking).ConfigureAwait(false);
			}

			return Json(new ScoreExerciseOperationResult { Status = "ok" });
		}

		private async Task NotifyAboutManualExerciseChecking(ManualExerciseChecking checking)
		{
			var isRecheck = notificationsRepo.FindNotifications<PassedManualExerciseCheckingNotification>(n => n.CheckingId == checking.Id).Any();
			var notification = new PassedManualExerciseCheckingNotification
			{
				Checking = checking,
				IsRecheck = isRecheck,
			};
			await notificationsRepo.AddNotification(checking.CourseId, notification, User.Identity.GetUserId()).ConfigureAwait(false);
		}

		[System.Web.Mvc.HttpPost]
		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		public async Task<ActionResult> SimpleScoreExercise(int submissionId, int exercisePercent, bool ignoreNewestSubmission = false, int? updateCheckingId = null)
		{
			var submission = userSolutionsRepo.FindSubmissionById(submissionId);
			var courseId = submission.CourseId;
			var slideId = submission.SlideId;
			var userId = submission.UserId;
			var currsntUserId = User.Identity.GetUserId();

			if (!User.HasAccessFor(courseId, CourseRole.Instructor))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			var slide = courseManager.FindCourse(courseId)?.FindSlideById(slideId, true) as ExerciseSlide;
			if (slide == null)
				return new HttpStatusCodeResult(HttpStatusCode.NotFound);

			if (!ignoreNewestSubmission && !updateCheckingId.HasValue)
			{
				var lastAcceptedSubmission = userSolutionsRepo.GetAllAcceptedSubmissionsByUser(courseId, slideId, userId).OrderByDescending(s => s.Timestamp).FirstOrDefault();
				if (lastAcceptedSubmission != null && lastAcceptedSubmission.Id != submission.Id)
					return Json(
						new SimpleScoreExerciseResult
						{
							Status = "error",
							Error = "has_newest_submission",
							SubmissionId = lastAcceptedSubmission.Id,
							SubmissionDate = lastAcceptedSubmission.Timestamp.ToAgoPrettyString(true)
						});
			}

			/* TODO: check if 0 <= exerciseScore <= 100 */

			await slideCheckingsRepo.RemoveWaitingManualCheckings<ManualExerciseChecking>(courseId, slideId, userId).ConfigureAwait(false);
			ManualExerciseChecking checking;
			if (updateCheckingId.HasValue)
				checking = slideCheckingsRepo.FindManualCheckingById<ManualExerciseChecking>(updateCheckingId.Value);
			else
				checking = await slideCheckingsRepo.AddManualExerciseChecking(courseId, slideId, userId, submission).ConfigureAwait(false);
			await slideCheckingsRepo.LockManualChecking(checking, User.Identity.GetUserId()).ConfigureAwait(false);
			await slideCheckingsRepo.MarkManualExerciseCheckingAsChecked(checking, exercisePercent).ConfigureAwait(false);
			/* 100%-score sets ProhibitFurtherChecking to true */
			if (exercisePercent == 100)
				await slideCheckingsRepo.ProhibitFurtherExerciseManualChecking(checking.CourseId, checking.UserId, checking.SlideId).ConfigureAwait(false);

			await visitsRepo.UpdateScoreForVisit(courseId, slide, userId).ConfigureAwait(false);

			await NotifyAboutManualExerciseChecking(checking).ConfigureAwait(false);

			return Json(
				new SimpleScoreExerciseResult
				{
					Status = "ok",
					Percent = exercisePercent,
					CheckingId = checking.Id,
				});
		}

		public ActionResult SubmissionsPanel(string courseId, Guid slideId, string userId = "", int? currentSubmissionId = null, bool canTryAgain = true)
		{
			var isInstructor = User.HasAccessFor(courseId, CourseRole.Instructor);
			if (!isInstructor)
				userId = "";

			if (userId == "")
				userId = User.Identity.GetUserId();

			var slide = courseManager.GetCourse(courseId).FindSlideById(slideId, isInstructor);
			var submissions = userSolutionsRepo.GetAllAcceptedSubmissionsByUser(courseId, slideId, userId).ToList();

			return PartialView(new ExerciseSubmissionsPanelModel(courseId, slide)
			{
				Submissions = submissions,
				CurrentSubmissionId = currentSubmissionId,
				CanTryAgain = canTryAgain,
			});
		}

		private ExerciseBlockData CreateExerciseBlockData(Course course, Slide slide, UserExerciseSubmission submission, bool onlyAccepted, string currentUserId)
		{
			var userId = submission?.UserId ?? currentUserId;
			var visit = visitsRepo.FindVisit(course.Id, slide.Id, userId);

			var solution = submission?.SolutionCode.Text;
			if (string.IsNullOrEmpty(solution))
			{
				/* I.e. after clicking on `Try again` button solution is empty. Let's try to show last sent submission */
				var lastSubmission = userSolutionsRepo.GetAllSubmissionsByUser(course.Id, slide.Id, currentUserId).OrderByDescending(s => s.Timestamp).FirstOrDefault();
				solution = lastSubmission?.SolutionCode.Text;
			}

			var submissionReviews = submission?.GetAllReviews();

			var hasUncheckedReview = submission?.ManualCheckings.Any(c => !c.IsChecked) ?? false;
			var hasCheckedReview = submission?.ManualCheckings.Any(c => c.IsChecked) ?? false;
			var reviewState = hasCheckedReview ? ExerciseReviewState.Reviewed :
				hasUncheckedReview ? ExerciseReviewState.WaitingForReview :
				ExerciseReviewState.NotReviewed;

			var submissions = onlyAccepted ?
				userSolutionsRepo.GetAllAcceptedSubmissionsByUser(course.Id, slide.Id, userId) :
				userSolutionsRepo.GetAllSubmissionsByUser(course.Id, slide.Id, userId);

			return new ExerciseBlockData(course.Id, (ExerciseSlide)slide, (visit?.IsSkipped ?? false) || (visit?.IsPassed ?? false), solution)
			{
				Url = Url,
				Reviews = submissionReviews?.ToList() ?? new List<ExerciseCodeReview>(),
				ReviewState = reviewState,
				IsGuest = string.IsNullOrEmpty(currentUserId),
				SubmissionSelectedByUser = submission,
				Submissions = submissions.ToList(),
				CurrentUser = usersRepo.FindUserById(User.Identity.GetUserId())
			};
		}

		private UserExerciseSubmission GetExerciseSubmissionShownByDefault(string courseId, Guid slideId, string userId, bool allowNotAccepted = false)
		{
			var submissions = userSolutionsRepo.GetAllAcceptedSubmissionsByUser(courseId, slideId, userId).ToList();
			var lastSubmission = submissions.LastOrDefault(s => s.ManualCheckings != null && s.ManualCheckings.Any()) ??
								submissions.LastOrDefault(s => s.AutomaticCheckingIsRightAnswer);
			if (lastSubmission == null && allowNotAccepted)
				lastSubmission = userSolutionsRepo.GetAllSubmissionsByUser(courseId, slideId, userId).ToList().LastOrDefault();
			return lastSubmission;
		}

		[System.Web.Mvc.AllowAnonymous]
		public ActionResult Submission(string courseId, Guid slideId, string userId = null, int? submissionId = null, int? manualCheckingId = null, bool isLti = false, bool showOutput = false, bool instructorView = false, bool onlyAccepted = true)
		{
			var isInstructor = User.HasAccessFor(courseId, CourseRole.Instructor);
			if (!isInstructor)
				instructorView = false;

			var currentUserId = userId ?? (User.Identity.IsAuthenticated ? User.Identity.GetUserId() : "");
			UserExerciseSubmission submission = null;
			if (submissionId.HasValue && submissionId.Value > 0)
			{
				submission = userSolutionsRepo.FindSubmissionById(submissionId.Value);
				if (submission == null)
					return HttpNotFound();
				if (!string.Equals(courseId, submission.CourseId, StringComparison.OrdinalIgnoreCase))
					return HttpNotFound();
				if (slideId != submission.SlideId)
					return HttpNotFound();
				if (!User.HasAccessFor(courseId, CourseRole.Instructor) && submission.UserId != currentUserId)
					return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
			}
			else if (!submissionId.HasValue && !manualCheckingId.HasValue)
			{
				submission = GetExerciseSubmissionShownByDefault(courseId, slideId, currentUserId, instructorView);
			}

			var course = courseManager.GetCourse(courseId);
			var slide = course.FindSlideById(slideId, isInstructor);
			if (slide == null)
				return HttpNotFound();

			ManualExerciseChecking manualChecking = null;
			if (User.HasAccessFor(courseId, CourseRole.Instructor) && manualCheckingId.HasValue)
			{
				manualChecking = slideCheckingsRepo.FindManualCheckingById<ManualExerciseChecking>(manualCheckingId.Value);
			}

			if (manualChecking != null && !submissionId.HasValue)
				submission = manualChecking.Submission;

			var model = CreateExerciseBlockData(course, slide, submission, onlyAccepted, currentUserId);
			model.IsLti = isLti;
			model.ShowOutputImmediately = showOutput;
			model.InstructorView = instructorView;
			model.ShowOnlyAccepted = onlyAccepted;
			if (manualChecking != null)
			{
				if (manualChecking.CourseId.EqualsIgnoreCase(courseId))
				{
					model.ManualChecking = manualChecking;
					model.Reviews = submission?.GetAllReviews() ?? new List<ExerciseCodeReview>();
				}
				model.TopUserReviewComments = slideCheckingsRepo.GetTopUserReviewComments(course.Id, slide.Id, currentUserId, 20);
				model.TopOtherUsersReviewComments = slideCheckingsRepo.GetTopOtherUsersReviewComments(course.Id, slide.Id, currentUserId, 10, model.TopUserReviewComments);
			}

			return PartialView(model);
		}
		
		public ActionResult LastReviewComments(string courseId, Guid slideId, string userId)
		{
			var reviewedSubmission = userSolutionsRepo
				.GetAllAcceptedSubmissionsByUser(courseId, new []{slideId}, userId)
				.Where(s => s.ManualCheckings.Any(c => c.IsChecked))
				.OrderByDescending(s => s.Timestamp)
				.FirstOrDefault();
			var lastManualChecking = reviewedSubmission?.ManualCheckings.OrderBy(c => c.Timestamp).LastOrDefault(c => c.IsChecked);

			if (lastManualChecking == null || !lastManualChecking.NotDeletedReviews.Any())
				return new EmptyResult();
			return PartialView("~/Views/Exercise/_ExerciseLastReviewComments.cshtml",
				new ExerciseLastReviewCommentModel
				{
					ReviewedSubmission = reviewedSubmission,
					NotDeletedReviews = lastManualChecking.NotDeletedReviews
				});
		}

		public ActionResult ExerciseScoreForm(BlockRenderContext context)
		{
			var checking = (ManualExerciseChecking)context.ManualChecking;
			var prevReviewPercent = slideCheckingsRepo.GetUserReviewPercentForExerciseSlide(
				context.Course.Id,
				context.Slide as ExerciseSlide,
				checking.UserId,
				checking.Submission.Timestamp);
			var model = new ExerciseScoreFormModel (
				context.Course.Id,
				(ExerciseSlide)context.Slide,
				checking,
				context.ManualCheckingsLeftInQueue,
				prevReviewPercent,
				context.GroupsIds,
				isCurrentSubmissionChecking: (context.VersionId == null || checking.Submission.Id == context.VersionId) && !context.IsManualCheckingReadonly,
				defaultProhibitFurtherReview: context.DefaultProhibitFurtherReview
			);
			return PartialView("~/Views/Exercise/_ExerciseScoreForm.cshtml", model);
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		public ActionResult StudentSubmissions(string courseId, Guid slideId)
		{
			return PartialView(GetStudentSubmissionsModel(courseId, slideId, ""));
		}

		[ULearnAuthorize(MinAccessLevel = CourseRole.Instructor)]
		public ActionResult StudentSubmissionsTable(string courseId, Guid slideId, string name)
		{
			var model = GetStudentSubmissionsModel(courseId, slideId, name);
			model.ShowAll = true;
			return PartialView(model);
		}

		private StudentSubmissionsModel GetStudentSubmissionsModel(string courseId, Guid slideId, string name)
		{
			const int maxUsersCount = 30;

			var course = courseManager.GetCourse(courseId);
			var slide = course.GetSlideById(slideId, true) as ExerciseSlide;

			if (slide == null)
				throw new HttpResponseException(HttpStatusCode.NotFound);

			var canViewAllSubmissions = User.HasAccessFor(courseId, CourseRole.CourseAdmin) || User.HasCourseAccess(courseId, CourseAccessType.ViewAllStudentsSubmissions);
			var hasFilterByName = !string.IsNullOrEmpty(name);

			/* By default show members of `my` groups, but if filter is enabled then course admin's and users with special access can view any student's submissions */

			SubmissionsFilterOptions filterOptions;
			var slideIdInList = new List<Guid> { slideId };
			var visitedUserIds = visitsRepo.GetVisitsInPeriod(new VisitsFilterOptions { CourseId = courseId, SlidesIds = slideIdInList, PeriodStart = DateTime.MinValue, PeriodFinish = DateTime.MaxValue })
				.Select(v => v.UserId)
				.ToList();
			if (hasFilterByName && canViewAllSubmissions)
			{
				/* Get all members who has visits to this slide */
				filterOptions = new SubmissionsFilterOptions
				{
					CourseId = courseId,
					UserIds = visitedUserIds,
					SlidesIds = slideIdInList,
				};
			}
			else
			{
				/* Get members of `my` groups */
				filterOptions = ControllerUtils.GetFilterOptionsByGroup<SubmissionsFilterOptions>(groupsRepo, User, courseId, groupsIds: new List<string>());
				filterOptions.SlidesIds = slideIdInList;
				/* Filter out only users with visits to this slide */
				filterOptions.UserIds = filterOptions.UserIds.Intersect(visitedUserIds).ToList();
			}

			if (hasFilterByName)
			{
				var filteredUserIds = usersRepo.FilterUsersByNamePrefix(name);
				filterOptions.UserIds = filterOptions.UserIds.Intersect(filteredUserIds).ToList();
			}

			filterOptions.UserIds = filterOptions.UserIds.Take(maxUsersCount).ToList();

			var submissions = userSolutionsRepo.GetAllSubmissionsByUsers(filterOptions);
			var submissionsByUser = submissions.ToList().GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.ToList()).ToDefaultDictionary(); // NOTE: ToList because Include not work with GroupBy

			var scores = visitsRepo.GetScore(courseId, slideId, filterOptions.UserIds);

			var userGroups = groupsRepo.GetUsersGroupsNamesAsStrings(courseId, filterOptions.UserIds, User, actual: true, archived: false).ToDefaultDictionary();

			return new StudentSubmissionsModel
			{
				CourseId = courseId,
				Slide = slide,
				Users = usersRepo.GetUsersByIds(filterOptions.UserIds).ToDictionary(u => u.Id),
				SubmissionsByUser = submissionsByUser,
				Scores = scores,
				HasFilterByName = hasFilterByName,
				UserGroups = userGroups,
			};
		}

		[System.Web.Mvc.AllowAnonymous]
		public ActionResult StudentZip(string courseId, Guid slideId)
		{
			var isInstructor = User.HasAccessFor(courseId, CourseRole.Instructor);
			var slide = courseManager.FindCourse(courseId)?.FindSlideById(slideId, isInstructor);
			if (!(slide is ExerciseSlide))
				return HttpNotFound();

			var exerciseSlide = slide as ExerciseSlide;
			if (exerciseSlide.Exercise is SingleFileExerciseBlock)
				return HttpNotFound();
			if ((exerciseSlide.Exercise as UniversalExerciseBlock)?.NoStudentZip ?? false)
				return HttpNotFound();

			var zipFile = exerciseStudentZipsCache.GenerateOrFindZip(courseId, exerciseSlide);

			var block = exerciseSlide.Exercise;
			var fileName = (block as CsProjectExerciseBlock)?.CsprojFile.Name ?? new DirectoryInfo((block as UniversalExerciseBlock).ExerciseDirPath).Name;
			return File(zipFile.FullName, "application/zip", fileName + ".zip");
		}
	}

	[DataContract]
	public class CodeReviewOperationResult
	{
		[DataMember(Name = "status")]
		public string Status { get; set; }
	}
	
	[DataContract]
	public class ScoreExerciseOperationResult
	{
		[DataMember(Name = "status")]
		public string Status { get; set; }
		
		[DataMember(Name = "redirect")]
		public string Redirect { get; set; }
	}

	[DataContract]
	public class SimpleScoreExerciseResult
	{
		[DataMember(Name = "status")]
		public string Status { get; set; }

		[DataMember(Name = "error")]
		public string Error { get; set; }

		[DataMember(Name = "submissionId")]
		public int SubmissionId { get; set; }

		[DataMember(Name = "submissionDate")]
		public string SubmissionDate { get; set; }

		[DataMember(Name = "percent")]
		public int Percent { get; set; }

		[DataMember(Name = "checkedQueueUrl")]
		public string CheckedQueueUrl { get; set; }

		[DataMember(Name = "checkingId")]
		public int CheckingId { get; set; }
	}

	public class ReviewInfo
	{
		[AllowHtml]
		public string Comment { get; set; }

		public int StartLine { get; set; }
		public int StartPosition { get; set; }
		public int FinishLine { get; set; }
		public int FinishPosition { get; set; }
	}

	public class ExerciseSubmissionsPanelModel
	{
		public ExerciseSubmissionsPanelModel(string courseId, Slide slide)
		{
			CourseId = courseId;
			Slide = slide;

			Submissions = new List<UserExerciseSubmission>();
			CurrentSubmissionId = null;
			CanTryAgain = true;
			ShowButtons = true;
			SelectControlName = "version";
		}

		public string CourseId { get; set; }
		public Slide Slide { get; set; }
		public List<UserExerciseSubmission> Submissions { get; set; }
		public int? CurrentSubmissionId { get; set; }
		public bool CanTryAgain { get; set; }
		public bool ShowButtons { get; set; }
		public string SelectControlName { get; set; }

		[CanBeNull]
		public Func<UserExerciseSubmission, string> GetSubmissionDescription { get; set; }

		/* By default it's Url.RouteUrl("Course.SlideById", new { Model.CourseId, slideId = Model.Slide.Url }) */
		[CanBeNull]
		public string FormUrl { get; set; }
	}

	public class ExerciseControlsModel
	{
		public ExerciseControlsModel(string courseId, ExerciseSlide slide)
		{
			CourseId = courseId;
			Slide = slide;
		}

		public string CourseId;
		public ExerciseSlide Slide;
		public AbstractExerciseBlock Block => Slide.Exercise;

		public bool IsLti = false;
		public bool IsCodeEditableAndSendable = true;
		public bool DebugView = false;
		public bool CanShowOutput = false;
		public bool IsShowOutputButtonActive = false;
		public string AcceptedSolutionsAction = "";
		public string RunSolutionUrl = "";
		public string UseHintUrl = "";

		public bool HideShowSolutionsButton => Block.HideShowSolutionsButton;
	}

	public class ExerciseScoreFormModel
	{
		public ExerciseScoreFormModel(string courseId, ExerciseSlide slide, ManualExerciseChecking checking,
			int manualCheckingsLeftInQueueInQueue, int? prevReviewPercent, List<string> groupsIds = null,
			bool isCurrentSubmissionChecking = false, bool defaultProhibitFurtherReview = true)
		{
			CourseId = courseId;
			Slide = slide;
			Checking = checking;
			ManualCheckingsLeftInQueue = manualCheckingsLeftInQueueInQueue;
			GroupsIds = groupsIds;
			IsCurrentSubmissionChecking = isCurrentSubmissionChecking;
			DefaultProhibitFurtherReview = defaultProhibitFurtherReview;
			PrevReviewPercent = prevReviewPercent;
		}

		public string CourseId { get; set; }
		public ExerciseSlide Slide { get; set; }
		public ManualExerciseChecking Checking { get; set; }
		public List<string> GroupsIds { get; set; }
		public string GroupsIdsJoined => string.Join(",", GroupsIds ?? new List<string>());
		public bool IsCurrentSubmissionChecking { get; set; }
		public bool DefaultProhibitFurtherReview { get; set; }
		public int ManualCheckingsLeftInQueue { get; set; }
		public int? PrevReviewPercent { get; set; }
	}

	public class ExerciseLastReviewCommentModel
	{
		public UserExerciseSubmission ReviewedSubmission { get; set; }
		public List<ExerciseCodeReview> NotDeletedReviews { get; set; }
	}

	public class StudentSubmissionsModel
	{
		public string CourseId { get; set; }
		public ExerciseSlide Slide { get; set; }

		public Dictionary<string, ApplicationUser> Users { get; set; }

		public DefaultDictionary<string, List<UserExerciseSubmission>> SubmissionsByUser { get; set; }

		public Dictionary<string, int> Scores { get; set; }

		public bool HasFilterByName { get; set; }

		public DefaultDictionary<string, string> UserGroups { get; set; }

		public bool ShowAll { get; set; }
	}
}