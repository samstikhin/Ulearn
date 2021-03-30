using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Database.Models;
using JetBrains.Annotations;
using Ulearn.Common;

namespace Database.DataContexts
{
	public class CommentsRepo
	{
		private readonly ULearnDb db;
		private readonly UserRolesRepo userRolesRepo;

		public CommentsRepo()
			: this(new ULearnDb())
		{
		}

		public CommentsRepo(ULearnDb db)
		{
			this.db = db;
			userRolesRepo = new UserRolesRepo(db);
		}

		public async Task<Comment> AddComment(string authorId, string courseId, Guid slideId, int parentCommentId, bool isForInstructorsOnly, string commentText)
		{
			var commentsPolicy = GetCommentsPolicy(courseId);
			var isInstructor = userRolesRepo.HasUserAccessToCourse(authorId, courseId, CourseRole.Instructor);
			var isApproved = commentsPolicy.ModerationPolicy == CommentModerationPolicy.Postmoderation || isInstructor;

			/* Instructors' replies are automaticly correct */
			var isReply = parentCommentId != -1;
			var isCorrectAnswer = isReply && isInstructor && !isForInstructorsOnly;

			var comment = db.Comments.Create();
			comment.AuthorId = authorId;
			comment.CourseId = courseId;
			comment.SlideId = slideId;
			comment.ParentCommentId = parentCommentId;
			comment.Text = commentText;
			comment.IsApproved = isApproved;
			comment.IsCorrectAnswer = isCorrectAnswer;
			comment.IsForInstructorsOnly = isForInstructorsOnly;
			comment.PublishTime = DateTime.Now;
			db.Comments.Add(comment);
			await db.SaveChangesAsync();

			return db.Comments.Find(comment.Id);
		}

		[CanBeNull]
		public Comment FindCommentById(int commentId)
		{
			return db.Comments.Find(commentId);
		}

		public IEnumerable<Comment> GetSlideComments(string courseId, Guid slideId)
		{
			return db.Comments.Where(x => x.CourseId == courseId && x.SlideId == slideId && !x.IsDeleted);
		}

		public IEnumerable<Comment> GetSlidesComments(string courseId, IEnumerable<Guid> slidesIds)
		{
			return db.Comments.Where(x => x.CourseId == courseId && slidesIds.Contains(x.SlideId) && !x.IsDeleted);
		}

		public IEnumerable<Comment> GetCourseComments(string courseId)
		{
			return db.Comments.Where(x => x.CourseId == courseId && !x.IsDeleted);
		}

		///<returns>(likesCount, isLikedByThisUsed)</returns>
		public async Task<Tuple<int, bool>> LikeComment(int commentId, string userId)
		{
			var commentForLike = db.Comments.Find(commentId);
			if (commentForLike == null)
				throw new Exception("Comment " + commentId + " not found");

			return await FuncUtils.TrySeveralTimesAsync(() => TryLikeComment(commentId, userId, commentForLike), 3);
		}

		private async Task<Tuple<int, bool>> TryLikeComment(int commentId, string userId, Comment commentForLike)
		{
			int likesCount;
			bool votedAlready;
			using (var transaction = db.Database.BeginTransaction())
			{
				var hisLike = db.CommentLikes.FirstOrDefault(x => x.UserId == userId && x.CommentId == commentId);
				votedAlready = hisLike != null;
				likesCount = commentForLike.Likes.Count;
				if (votedAlready)
				{
					db.CommentLikes.Remove(hisLike);
					likesCount--;
				}
				else
				{
					db.CommentLikes.Add(new CommentLike
					{
						UserId = userId,
						CommentId = commentId,
						Timestamp = DateTime.Now,
					});
					likesCount++;
				}

				await db.SaveChangesAsync();

				transaction.Commit();
			}

			return Tuple.Create(likesCount, !votedAlready);
		}

		public IEnumerable<CommentLike> GetCommentLikes(Comment comment)
		{
			return db.CommentLikes.Where(x => x.CommentId == comment.Id);
		}

		public IEnumerable<ApplicationUser> GetCommentLikers(Comment comment)
		{
			return GetCommentLikes(comment).Select(x => x.User);
		}

		public int GetCommentLikesCount(Comment comment)
		{
			return db.CommentLikes.Count(x => x.CommentId == comment.Id);
		}

		/// <returns>{commentId => likesCount}</returns>
		public Dictionary<int, int> GetCommentsLikesCounts(IEnumerable<Comment> comments)
		{
			var commentsIds = comments.Select(x => x.Id).ToImmutableHashSet();
			return db.CommentLikes.Where(x => commentsIds.Contains(x.CommentId))
				.GroupBy(x => x.CommentId)
				.Select(g => new { g.Key, Count = g.Count()})
				.ToDictionary(p => p.Key, p => p.Count);
		}

		public IEnumerable<int> GetSlideCommentsLikedByUser(string courseId, Guid slideId, string userId)
		{
			return db.CommentLikes.Where(x => x.UserId == userId && x.Comment.CourseId == courseId && x.Comment.SlideId == slideId).Select(x => x.CommentId);
		}

		public IEnumerable<int> GetCourseCommentsLikedByUser(string courseId, string userId)
		{
			return db.CommentLikes.Where(x => x.UserId == userId && x.Comment.CourseId == courseId).Select(x => x.CommentId);
		}

		public CommentsPolicy GetCommentsPolicy(string courseId)
		{
			var policy = db.CommentsPolicies.FirstOrDefault(x => x.CourseId == courseId);
			return policy ?? new CommentsPolicy
			{
				CourseId = courseId,
				IsCommentsEnabled = true,
				ModerationPolicy = CommentModerationPolicy.Postmoderation,
				OnlyInstructorsCanReply = false,
			};
		}

		public async Task SaveCommentsPolicy(CommentsPolicy policy)
		{
			using (var transaction = db.Database.BeginTransaction())
			{
				var query = db.CommentsPolicies.Where(x => x.CourseId == policy.CourseId);
				if (query.Any())
				{
					db.CommentsPolicies.Remove(query.First());
					await db.SaveChangesAsync();
				}

				db.CommentsPolicies.Add(policy);
				await db.SaveChangesAsync();
				transaction.Commit();
			}
		}

		public async Task<Comment> ModifyComment(int commentId, Action<Comment> modifyAction)
		{
			var comment = db.Comments.Find(commentId);
			modifyAction(comment);
			await db.SaveChangesAsync();
			return comment;
		}

		public async Task<Comment> EditCommentText(int commentId, string newText)
		{
			return await ModifyComment(commentId, c => c.Text = newText);
		}

		public async Task ApproveComment(int commentId, bool isApproved)
		{
			await ModifyComment(commentId, c => c.IsApproved = isApproved);
		}

		public async Task DeleteComment(int commentId)
		{
			await ModifyComment(commentId, c => c.IsDeleted = true);
		}

		public async Task<Comment> RestoreComment(int commentId)
		{
			return await ModifyComment(commentId, c => c.IsDeleted = false);
		}

		public async Task PinComment(int commentId, bool isPinned)
		{
			await ModifyComment(commentId, c => c.IsPinnedToTop = isPinned);
		}

		public async Task MarkCommentAsCorrectAnswer(int commentId, bool isCorrect = true)
		{
			await ModifyComment(commentId, c => c.IsCorrectAnswer = isCorrect);
		}

		public bool IsUserAddedMaxCommentsInLastTime(string userId, int maxCount, TimeSpan lastTime)
		{
			var lastComments = db.Comments.Where(x => x.AuthorId == userId)
				.OrderByDescending(x => x.PublishTime)
				.Take(maxCount)
				.OrderBy(x => x.PublishTime);
			if (lastComments.Count() < maxCount || maxCount <= 0)
				return false;
			var boundComment = lastComments.FirstOrDefault();
			return boundComment == null || boundComment.PublishTime >= DateTime.Now - lastTime;
		}
	}
}