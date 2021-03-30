using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using System.Web.Mvc;
using Database;
using Database.DataContexts;
using Database.Models;
using Microsoft.AspNet.Identity;
using uLearn.Web.FilterAttributes;
using uLearn.Web.Models;
using Ulearn.Core;
using Ulearn.Core.Courses.Slides;
using Ulearn.Core.Courses.Slides.Exercises;
using Ulearn.Core.Courses.Slides.Quizzes;

namespace uLearn.Web.Controllers
{
	public class CommentsController : Controller
	{
		private readonly CourseManager courseManager = WebCourseManager.Instance;
		private readonly CommentsRepo commentsRepo;
		private readonly NotificationsRepo notificationsRepo;
		private readonly CoursesRepo coursesRepo;
		private readonly SystemAccessesRepo systemAccessesRepo;
		private readonly UserRolesRepo userRolesRepo;
		private readonly UserManager<ApplicationUser> userManager;

		public CommentsController(ULearnDb db)
		{
			commentsRepo = new CommentsRepo(db);
			userManager = new ULearnUserManager(db);
			notificationsRepo = new NotificationsRepo(db);
			coursesRepo = new CoursesRepo(db);
			systemAccessesRepo = new SystemAccessesRepo(db);
			userRolesRepo = new UserRolesRepo(db);
		}

		public CommentsController()
			: this(new ULearnDb())
		{
		}

		public ActionResult SlideComments(string courseId, Guid slideId, bool openInstructorsComments = false)
		{
			var course = courseManager.GetCourse(courseId);
			var slide = course.FindSlideById(slideId, true);
			if (slide == null)
				return Content("");

			var userId = User.Identity.GetUserId();
			var systemAccesses = systemAccessesRepo.GetSystemAccesses(userId);
			var courseAccesses = coursesRepo.GetCourseAccesses(courseId, userId);
			var slideType = GetSlideType(slide);

			var model = new SlideCommentsModel
			{
				CourseId = courseId,
				Slide = slide,
				CurrentUser = User.Identity.IsAuthenticated ? userManager.FindById(userId) : null,
				OpenInstructorsComments = openInstructorsComments,
				CourseAccesses = courseAccesses,
				SystemAccesses = systemAccesses,
				SlideType = slideType
			};
			return PartialView(model);
		}

		private static SlideType GetSlideType(Slide slide)
		{
			switch (slide)
			{
				case ExerciseSlide _:
					return SlideType.Exercise;
				case QuizSlide _:
					return SlideType.Quiz;
				default:
					return SlideType.Lesson;
			}
		}

		private bool CanModerateComments(string userId, string courseId)
		{
			if (!userRolesRepo.IsSystemAdministrator(userId))
				return false;

			var hasCourseAccessForCommentEditing = coursesRepo.HasCourseAccess(userId, courseId, CourseAccessType.EditPinAndRemoveComments);
			return userRolesRepo.HasUserAccessToCourse(userId, courseId, CourseRole.CourseAdmin) || hasCourseAccessForCommentEditing;
		}

		private bool CanAddCommentHere(string userId, string courseId, bool isReply)
		{
			if (!User.Identity.IsAuthenticated)
				return false;

			var commentsPolicy = commentsRepo.GetCommentsPolicy(courseId);
			var isInstructor = userRolesRepo.HasUserAccessToCourse(userId, courseId, CourseRole.Instructor);

			if (!isInstructor && !commentsPolicy.IsCommentsEnabled)
				return false;

			if (isReply && !isInstructor && commentsPolicy.OnlyInstructorsCanReply)
				return false;

			return true;
		}

		private bool CanViewAndAddCommentsForInstructorsOnly(string userId, string courseId)
		{
			return userRolesRepo.HasUserAccessToCourse(userId, courseId, CourseRole.Instructor);
		}

		private bool CanAddCommentNow(string userId, string courseId)
		{
			// Instructors have unlimited comments
			if (userRolesRepo.HasUserAccessToCourse(userId, courseId, CourseRole.Instructor))
				return true;

			var commentsPolicy = commentsRepo.GetCommentsPolicy(courseId);
			return !commentsRepo.IsUserAddedMaxCommentsInLastTime(userId,
				commentsPolicy.MaxCommentsCountInLastTime,
				commentsPolicy.LastTimeForMaxCommentsLimit);
		}

		[ULearnAuthorize]
		[HttpPost]
		[ValidateInput(false)]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> AddComment(string courseId, Guid slideId, bool forInstructorsOnly, string commentText, string parentCommentId)
		{
			var parentCommentIdInt = -1;
			if (parentCommentId != null)
				int.TryParse(parentCommentId, out parentCommentIdInt);
			var userId = User.Identity.GetUserId();

			if (!CanAddCommentHere(userId, courseId, parentCommentIdInt != -1))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			if (!CanAddCommentNow(userId, courseId))
			{
				return Json(new
				{
					status = "too-fast",
					message = "Вы комментируете слишком быстро. Подождите немного...",
				});
			}

			if (commentText.Length > CommentsPolicy.MaxCommentLength)
			{
				return Json(new
				{
					status = "too-long",
					message = "Слишком длинный комментарий. Попробуйте сократить мысль.",
				});
			}

			if (forInstructorsOnly && !CanViewAndAddCommentsForInstructorsOnly(userId, courseId))
			{
				forInstructorsOnly = false;
			}

			var comment = await commentsRepo.AddComment(userId, courseId, slideId, parentCommentIdInt, forInstructorsOnly, commentText);
			if (comment.IsApproved)
				await NotifyAboutNewComment(comment);
			var canReply = CanAddCommentHere(userId, courseId, isReply: true);

			var canViewAuthorSubmissions = coursesRepo.HasCourseAccess(userId, courseId, CourseAccessType.ViewAllStudentsSubmissions) || userRolesRepo.HasUserAccessToCourse(userId, courseId, CourseRole.CourseAdmin);
			var canViewProfiles = systemAccessesRepo.HasSystemAccess(userId, SystemAccessType.ViewAllProfiles) || userRolesRepo.IsSystemAdministrator(userId);

			return PartialView("_Comment", new CommentViewModel
			{
				Comment = comment,
				LikesCount = 0,
				IsLikedByUser = false,
				Replies = new List<CommentViewModel>(),
				IsCommentVisibleForUser = true,
				CanEditAndDeleteComment = true,
				CanModerateComment = userRolesRepo.HasUserAccessToCourse(userId, courseId, CourseRole.Instructor),
				CanReply = canReply,
				CurrentUser = userManager.FindById(User.Identity.GetUserId()),
				CanViewAuthorProfile = canViewProfiles,
				CanViewAuthorSubmissions = canViewAuthorSubmissions,
			});
		}

		private async Task NotifyAboutNewComment(Comment comment)
		{
			var courseId = comment.CourseId;

			if (!comment.IsTopLevel())
			{
				var parentComment = commentsRepo.FindCommentById(comment.ParentCommentId);
				if (parentComment != null)
				{
					var replyNotification = new RepliedToYourCommentNotification
					{
						Comment = comment,
						ParentComment = parentComment,
					};
					await notificationsRepo.AddNotification(courseId, replyNotification, comment.AuthorId);
				}
			}

			/* Create NewCommentFromStudentFormYourGroupNotification later than RepliedToYourCommentNotification, because the last one is blocker for the first one.
			 * We don't send NewCommentNotification if there is a RepliedToYouCommentNotification */
			var commentFromYourGroupStudentNotification = new NewCommentFromYourGroupStudentNotification { Comment = comment };
			await notificationsRepo.AddNotification(courseId, commentFromYourGroupStudentNotification, comment.AuthorId);

			/* Create NewCommentNotification later than RepliedToYourCommentNotification and NewCommentFromYourGroupStudentNotification, because the last one is blocker for the first one.
			 * We don't send NewCommentNotification if there is a RepliedToYouCommentNotification or NewCommentFromYourGroupStudentNotification */
			var notification = comment.IsForInstructorsOnly
				? (Notification)new NewCommentForInstructorsOnlyNotification { Comment = comment }
				: new NewCommentNotification { Comment = comment };
			await notificationsRepo.AddNotification(courseId, notification, comment.AuthorId);
		}

		[ULearnAuthorize]
		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> LikeComment(int commentId)
		{
			var userId = User.Identity.GetUserId();
			var res = await commentsRepo.LikeComment(commentId, userId);

			await NotifyAboutLikedComment(commentId);

			return Json(new { likesCount = res.Item1, liked = res.Item2 });
		}

		private async Task NotifyAboutLikedComment(int commentId)
		{
			var comment = commentsRepo.FindCommentById(commentId);
			if (comment != null)
			{
				var userId = User.Identity.GetUserId();
				var notification = new LikedYourCommentNotification
				{
					Comment = comment,
					LikedUserId = userId,
				};
				await notificationsRepo.AddNotification(comment.CourseId, notification, userId);
			}
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> ApproveComment(int commentId, bool isApproved = true)
		{
			var comment = commentsRepo.FindCommentById(commentId);
			if (comment == null)
				return HttpNotFound();

			if (!CanModerateComments(User.Identity.GetUserId(), comment.CourseId))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			await commentsRepo.ApproveComment(commentId, isApproved);
			if (isApproved)
				await NotifyAboutNewComment(comment);
			return new HttpStatusCodeResult(HttpStatusCode.OK);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> PinComment(int commentId, bool isPinned)
		{
			var comment = commentsRepo.FindCommentById(commentId);
			if (comment == null)
				return HttpNotFound();

			if (!CanModerateComments(User.Identity.GetUserId(), comment.CourseId))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			await commentsRepo.PinComment(commentId, isPinned);
			return new HttpStatusCodeResult(HttpStatusCode.OK);
		}

		private bool CanEditAndDeleteComment(string userId, Comment comment)
		{
			if (comment == null)
				return false;

			return CanModerateComments(userId, comment.CourseId) || userId == comment.AuthorId;
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> DeleteComment(int commentId)
		{
			var comment = commentsRepo.FindCommentById(commentId);
			if (!CanEditAndDeleteComment(User.Identity.GetUserId(), comment))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			await commentsRepo.DeleteComment(commentId);
			return new HttpStatusCodeResult(HttpStatusCode.OK);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> RestoreComment(int commentId)
		{
			var comment = commentsRepo.FindCommentById(commentId);
			if (!CanEditAndDeleteComment(User.Identity.GetUserId(), comment))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			await commentsRepo.RestoreComment(commentId);
			return new HttpStatusCodeResult(HttpStatusCode.OK);
		}

		[ValidateInput(false)]
		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> EditCommentText(int commentId, string newText)
		{
			var comment = commentsRepo.FindCommentById(commentId);
			if (!CanEditAndDeleteComment(User.Identity.GetUserId(), comment))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			var newComment = await commentsRepo.EditCommentText(commentId, newText);
			return PartialView("_CommentText", newComment);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		[HandleHttpAntiForgeryException]
		public async Task<ActionResult> MarkAsCorrectAnswer(int commentId, bool isCorrect = true)
		{
			var comment = commentsRepo.FindCommentById(commentId);
			if (!CanEditAndDeleteComment(User.Identity.GetUserId(), comment))
				return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

			await commentsRepo.MarkCommentAsCorrectAnswer(commentId, isCorrect);
			return new HttpStatusCodeResult(HttpStatusCode.OK);
		}
	}

	public class SlideCommentsModel
	{
		public string CourseId { get; set; }
		public Slide Slide { get; set; }
		public ApplicationUser CurrentUser { get; set; }
		public bool OpenInstructorsComments { get; set; }
		public List<CourseAccess> CourseAccesses { get; set; }
		public List<SystemAccess> SystemAccesses { get; set; }
		public SlideType SlideType { get; set; }
	}
}