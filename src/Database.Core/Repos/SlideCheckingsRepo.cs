using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Database.Extensions;
using Database.Models;
using Database.Models.Quizzes;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ulearn.Core.Courses;
using Ulearn.Core.Courses.Slides.Exercises;

namespace Database.Repos
{
	public class SlideCheckingsRepo : ISlideCheckingsRepo
	{
		private readonly UlearnDb db;

		[ItemCanBeNull]
		private readonly Lazy<IVisitsRepo> visitsRepo;

		public SlideCheckingsRepo(UlearnDb db, [CanBeNull] IServiceProvider serviceProvider)
		{
			this.db = db;
			visitsRepo = new Lazy<IVisitsRepo>(() => serviceProvider?.GetRequiredService<IVisitsRepo>());
		}

		public async Task<ManualQuizChecking> AddManualQuizChecking(UserQuizSubmission submission, string courseId, Guid slideId, string userId)
		{
			var manualChecking = new ManualQuizChecking
			{
				Submission = submission,
				CourseId = courseId,
				SlideId = slideId,
				UserId = userId,
				Timestamp = DateTime.Now,
			};
			db.ManualQuizCheckings.Add(manualChecking);
			await db.SaveChangesAsync();

			return manualChecking;
		}

		public async Task<AutomaticQuizChecking> AddAutomaticQuizChecking(UserQuizSubmission submission, string courseId, Guid slideId, string userId, int automaticScore)
		{
			var automaticChecking = new AutomaticQuizChecking
			{
				Submission = submission,
				CourseId = courseId,
				SlideId = slideId,
				UserId = userId,
				Timestamp = DateTime.Now,
				Score = automaticScore,
			};
			db.AutomaticQuizCheckings.Add(automaticChecking);
			await db.SaveChangesAsync();

			return automaticChecking;
		}

		public async Task<bool> HasManualExerciseChecking(string courseId, Guid slideId, string userId, int submissionId)
		{
			return await db.ManualExerciseCheckings
				.AnyAsync(c => c.CourseId == courseId && c.UserId == userId && c.SlideId == slideId && c.SubmissionId == submissionId);
		}

		public async Task<ManualExerciseChecking> AddManualExerciseChecking(string courseId, Guid slideId, string userId, int submissionId)
		{
			var manualChecking = new ManualExerciseChecking
			{
				CourseId = courseId,
				SlideId = slideId,
				UserId = userId,
				Timestamp = DateTime.Now,
				SubmissionId = submissionId,
			};
			db.ManualExerciseCheckings.Add(manualChecking);

			await db.SaveChangesAsync();

			return manualChecking;
		}

		public async Task RemoveWaitingManualCheckings<T>(string courseId, Guid slideId, string userId, bool startTransaction = true) where T : AbstractManualSlideChecking
		{
			using (var transaction = startTransaction ? db.Database.BeginTransaction() : null)
			{
				var checkings = (await GetSlideCheckingsByUser<T>(courseId, slideId, userId)
						.ToListAsync())
					.Where(c => !c.IsChecked && !c.IsLocked)
					.ToList();
				foreach (var checking in checkings)
				{
					checking.PreRemove(db);
					db.Set<T>().Remove(checking);
				}

				await db.SaveChangesAsync();
				if (transaction != null)
					await transaction.CommitAsync();
			}
		}

		private IQueryable<T> GetSlideCheckingsByUser<T>(string courseId, Guid slideId, string userId) where T : AbstractSlideChecking
		{
			return db.Set<T>().Where(c => c.CourseId == courseId && c.SlideId == slideId && c.UserId == userId);
		}

		public async Task RemoveAttempts(string courseId, Guid slideId, string userId, bool saveChanges = true)
		{
			db.ManualQuizCheckings.RemoveSlideAction(courseId, slideId, userId);
			db.AutomaticQuizCheckings.RemoveSlideAction(courseId, slideId, userId);
			db.ManualExerciseCheckings.RemoveSlideAction(courseId, slideId, userId);
			db.AutomaticExerciseCheckings.RemoveSlideAction(courseId, slideId, userId);
			if (saveChanges)
				await db.SaveChangesAsync();
		}

		public async Task<bool> IsSlidePassed(string courseId, Guid slideId, string userId)
		{
			return await GetSlideCheckingsByUser<ManualQuizChecking>(courseId, slideId, userId).AnyAsync() ||
					await GetSlideCheckingsByUser<AutomaticQuizChecking>(courseId, slideId, userId).AnyAsync() ||
					await GetSlideCheckingsByUser<ManualExerciseChecking>(courseId, slideId, userId).AnyAsync() ||
					await GetSlideCheckingsByUser<AutomaticExerciseChecking>(courseId, slideId, userId).AnyAsync(c => c.IsRightAnswer);
		}

		#region Slide Score Calculating

		public async Task<(int Score, int? Percent)> GetExerciseSlideScoreAndPercent(string courseId, ExerciseSlide slide, string userId)
		{
			var hasAutomaticChecking = slide.Exercise.HasAutomaticChecking();
			if (hasAutomaticChecking)
			{
				var isRightAnswer = await GetSlideCheckingsByUser<AutomaticExerciseChecking>(courseId, slide.Id, userId)
					.AnyAsync(c => c.IsRightAnswer);
				if (!isRightAnswer)
					return (0, null);
			}
			var checkedScoresAndPercents = await GetCheckedScoresAndPercents(courseId, slide, userId, null);
			var automaticScore = slide.Scoring.PassedTestsScore;
			if (checkedScoresAndPercents.Count == 0)
				return (automaticScore, null);
			return GetScoreAndPercentByScoresAndPercents(slide, checkedScoresAndPercents);
		}

		public static (int Score, int? Percent) GetExerciseSubmissionManualCheckingsScoreAndPercent(IList<ManualExerciseChecking> manualCheckings, ExerciseSlide slide)
		{
			var checkedScoresAndPercents = manualCheckings
				.Where(c => c.IsChecked)
				.OrderBy(c => c.Timestamp)
				.Select(c => (c.Score, c.Percent))
				.ToList();
			return GetScoreAndPercentByScoresAndPercents(slide, checkedScoresAndPercents);
		}

		private static (int Score, int? Percent) GetScoreAndPercentByScoresAndPercents(
			ExerciseSlide slide,
			List<(int? Score, int? Percent)> checkedScoresAndPercents)
		{
			var automaticScore = slide.Scoring.PassedTestsScore;
			if (checkedScoresAndPercents.Count == 0)
				return (automaticScore, null);
			var scores = checkedScoresAndPercents
				.Where(p => p.Score != null)
				.Select(p => p.Score.Value)
				.ToList();
			var lastPercent = checkedScoresAndPercents
				.Select(p => p.Percent)
				.LastOrDefault(p => p != null) ?? 0;
			var scoreFormPercent = (int)Math.Ceiling(lastPercent / 100m * slide.Scoring.ScoreWithCodeReview);
			var scoreFormScore = scores.Count == 0 ? 0 : automaticScore + scores.Max();
			return scoreFormPercent >= scoreFormScore
				? (scoreFormPercent, lastPercent)
				: (scoreFormScore, (int)Math.Ceiling(scoreFormScore * 100m / slide.Scoring.ScoreWithCodeReview));
		}

		public async Task<int?> GetUserReviewPercentForExerciseSlide(string courseId, ExerciseSlide slide, string userId, DateTime? submissionBefore = null)
		{
			var checkedScoresAndPercents = await GetCheckedScoresAndPercents(courseId, slide, userId, submissionBefore);
			return GetScoreAndPercentByScoresAndPercents(slide, checkedScoresAndPercents).Percent;
		}

		private async Task<List<(int? Score, int? Percent)>> GetCheckedScoresAndPercents(string courseId, ExerciseSlide slide, string userId, DateTime? submissionBefore)
		{
			var query = GetSlideCheckingsByUser<ManualExerciseChecking>(courseId, slide.Id, userId)
				.Where(c => c.IsChecked);
			if (submissionBefore != null)
				query = query.Where(c => c.Submission.Timestamp < submissionBefore);
			var checkedScoresAndPercents = (await query
					.Select(c => new { c.Score, c.Percent, CheckingTimestamp = c.Timestamp, SubmissionTimestamp = c.Submission.Timestamp })
					.OrderBy(c => c.SubmissionTimestamp)
					.ThenBy(c => c.CheckingTimestamp)
					.Select(c => new { c.Score, c.Percent })
					.ToListAsync())
				.Select(c => (c.Score, c.Percent))
				.ToList();
			return checkedScoresAndPercents;
		}

		public async Task<List<(Guid SlideId, int Score, int Percent)>> GetPassedManualExerciseCheckingsScoresAndPercents(Course course, string userId)
		{
			var checkings = await db.ManualExerciseCheckings
				.Where(c => c.CourseId == course.Id && c.UserId == userId && c.IsChecked)
				.ToListAsync();
			var slides = course.GetSlides(false).OfType<ExerciseSlide>().Select(s => s.Id).ToHashSet();
			return checkings.GroupBy(s => s.SlideId)
				.Where(s => slides.Contains(s.Key))
				.Select(g =>
				{
					var checkedScoresAndPercents = g.Select(c => (c.Score, c.Percent)).ToList();
					var slide = course.GetSlideById(g.Key, false) as ExerciseSlide;
					var (score, percent) = GetScoreAndPercentByScoresAndPercents(slide, checkedScoresAndPercents);
					return (g.Key, score, percent.Value);
				}).ToList();
		}

		public async Task<int> GetUserScoreForQuizSlide(string courseId, Guid slideId, string userId)
		{
			var manualScore = await GetSlideCheckingsByUser<ManualQuizChecking>(courseId, slideId, userId).MaxAsync(c => (int?)c.Score) ?? 0;
			var automaticScore = await GetSlideCheckingsByUser<AutomaticQuizChecking>(courseId, slideId, userId).MaxAsync(c => (int?)c.Score) ?? 0;
			return automaticScore + manualScore;
		}

		#endregion

		public async Task<List<T>> GetManualCheckingQueue<T>(ManualCheckingQueueFilterOptions options) where T : AbstractManualSlideChecking
		{
			var query = GetManualCheckingQueueFilterQuery<T>(options);
			query = query.OrderByDescending(c => c.Timestamp);

			const int reserveForStartedReviews = 100;
			if (options.Count > 0)
				query = query.Take(options.Count + reserveForStartedReviews);

			IEnumerable<T> enumerable = await query.ToListAsync();
			// Отфильтровывает неактуальные начатые ревью
			enumerable = enumerable
				.GroupBy(g => new { g.UserId, g.SlideId })
				.Select(g => g.First())
				.OrderByDescending(c => c.Timestamp);
			if (options.Count > 0)
				enumerable = enumerable.Take(options.Count);

			return enumerable.ToList();
		}

		private IQueryable<T> GetManualCheckingQueueFilterQuery<T>(ManualCheckingQueueFilterOptions options) where T : AbstractManualSlideChecking
		{
			var query = db.Set<T>()
				.Include(c => c.User)
				.Where(c => c.CourseId == options.CourseId);
			if (options.OnlyChecked.HasValue)
				query = options.OnlyChecked.Value ? query.Where(c => c.IsChecked) : query.Where(c => !c.IsChecked);
			if (options.SlidesIds != null)
				query = query.Where(c => options.SlidesIds.Contains(c.SlideId));
			if (options.UserIds != null)
			{
				if (options.IsUserIdsSupplement)
					query = query.Where(c => !options.UserIds.Contains(c.UserId));
				else
					query = query.Where(c => options.UserIds.Contains(c.UserId));
			}

			return query;
		}

		/// For calculating not checked submissions as well as checked ones
		public async Task<int> GetQuizManualCheckingCount(string courseId, Guid slideId, string userId, DateTime? beforeTimestamp)
		{
			var queue = db.ManualQuizCheckings.Where(c =>
				c.CourseId == courseId
				&& c.SlideId == slideId
				&& c.UserId == userId
				&& !c.IgnoreInAttemptsCount);
			if (beforeTimestamp != null)
				queue = queue.Where(s => s.Timestamp < beforeTimestamp);
			return await queue.CountAsync();
		}

		public async Task<HashSet<Guid>> GetManualCheckingQueueSlideIds<T>(ManualCheckingQueueFilterOptions options)
			where T : AbstractManualSlideChecking
		{
			var query = GetManualCheckingQueueFilterQuery<T>(options);
			return (await query.Select(c => c.SlideId).Distinct().ToListAsync()).ToHashSet();
		}

		public async Task<T> FindManualCheckingById<T>(int id) where T : AbstractManualSlideChecking
		{
			return await db.Set<T>().FindAsync(id);
		}

		public async Task<bool> IsProhibitedToSendExerciseToManualChecking(string courseId, Guid slideId, string userId)
		{
			return await GetSlideCheckingsByUser<ManualExerciseChecking>(courseId, slideId, userId)
				.AnyAsync(c => c.ProhibitFurtherManualCheckings);
		}

		public async Task LockManualChecking<T>(T checkingItem, string lockedById) where T : AbstractManualSlideChecking
		{
			checkingItem.LockedById = lockedById;
			checkingItem.LockedUntil = DateTime.Now.Add(TimeSpan.FromMinutes(30));
			await db.SaveChangesAsync();
		}

		public async Task MarkManualQuizCheckingAsChecked(ManualQuizChecking queueItem, int score)
		{
			queueItem.LockedBy = null;
			queueItem.LockedUntil = null;
			queueItem.IsChecked = true;
			queueItem.Score = score;
			await db.SaveChangesAsync();
		}

		public async Task MarkManualExerciseCheckingAsChecked(ManualExerciseChecking queueItem, int percent)
		{
			queueItem.LockedBy = null;
			queueItem.LockedUntil = null;
			queueItem.IsChecked = true;
			queueItem.Percent = percent;
			await db.SaveChangesAsync().ConfigureAwait(false);
		}

		// Помечает оцененными посещенные но не оцененные старые ревью
		public async Task MarkManualExerciseCheckingAsCheckedBeforeThis(ManualExerciseChecking queueItem)
		{
			var itemsForMark = (await db.Set<ManualExerciseChecking>()
					.Include(c => c.Submission)
					.Where(c => c.CourseId == queueItem.CourseId && c.UserId == queueItem.UserId && c.SlideId == queueItem.SlideId && c.Timestamp < queueItem.Timestamp)
					.ToListAsync())
				.OrderBy(c => c.Submission.Timestamp)
				.ThenBy(c => c.Timestamp);
			int? percent = 0;
			int? score = 0;
			var changed = false;
			foreach (var item in itemsForMark)
			{
				if (item.IsChecked)
				{
					percent = item.Percent;
					score = item.Score;
				}
				else
				{
					item.LockedBy = null;
					item.LockedUntil = null;
					item.IsChecked = true;
					queueItem.Percent = percent;
					queueItem.Score = score;
					changed = true;
				}
			}

			if (changed)
				await db.SaveChangesAsync();
		}

		public async Task ProhibitFurtherExerciseManualChecking(string courseId, string userId, Guid slideId)
		{
			var checkings = await db.ManualExerciseCheckings
				.Where(c => c.CourseId == courseId && c.UserId == userId && c.SlideId == slideId && c.ProhibitFurtherManualCheckings)
				.ToListAsync();
			if (checkings.Count == 0)
				return;
			foreach (var checking in checkings)
				checking.ProhibitFurtherManualCheckings = true;
			await db.SaveChangesAsync();
		}

		public async Task ResetManualCheckingLimitsForUser(string courseId, string userId)
		{
			await DisableProhibitFurtherManualCheckings(courseId, userId);
			await NotCountOldAttemptsToQuizzesWithManualChecking(courseId, userId);
		}

		public async Task ResetAutomaticCheckingLimitsForUser(string courseId, string userId)
		{
			await NotCountOldAttemptsToQuizzesWithAutomaticChecking(courseId, userId);
			if (visitsRepo.Value != null)
				await visitsRepo.Value.UnskipAllSlides(courseId, userId);
		}

		public async Task DisableProhibitFurtherManualCheckings(string courseId, string userId, Guid? slideId = null)
		{
			var query = db.ManualExerciseCheckings
				.Where(c => c.CourseId == courseId && c.UserId == userId && c.ProhibitFurtherManualCheckings);
			if (slideId != null)
				query = query.Where(c => c.SlideId == slideId);
			var checkingsWithProhibitFurther = await query.ToListAsync();
			if (checkingsWithProhibitFurther.Count == 0)
				return;
			foreach (var checking in checkingsWithProhibitFurther)
				checking.ProhibitFurtherManualCheckings = false;
			await db.SaveChangesAsync();
		}

		public async Task NotCountOldAttemptsToQuizzesWithManualChecking(string courseId, string userId)
		{
			var checkings = await db.ManualQuizCheckings
				.Where(c => c.CourseId == courseId && c.UserId == userId && c.IsChecked)
				.ToListAsync();
			foreach (var checking in checkings)
				checking.IgnoreInAttemptsCount = true;
			await db.SaveChangesAsync();
		}

		public async Task NotCountOldAttemptsToQuizzesWithAutomaticChecking(string courseId, string userId)
		{
			var checkings = await db.AutomaticQuizCheckings
				.Where(c => c.CourseId == courseId && c.UserId == userId)
				.ToListAsync();
			foreach (var checking in checkings)
				checking.IgnoreInAttemptsCount = true;
			await db.SaveChangesAsync();
		}

		private async Task<ExerciseCodeReview> AddExerciseCodeReview(int? submissionId, [CanBeNull] ManualExerciseChecking checking, string userId, int startLine, int startPosition, int finishLine, int finishPosition, string comment, bool setAddingTime)
		{
			var review = db.ExerciseCodeReviews.Add(new ExerciseCodeReview
			{
				AuthorId = userId,
				Comment = comment,
				ExerciseCheckingId = checking?.Id,
				SubmissionId = submissionId,
				StartLine = startLine,
				StartPosition = startPosition,
				FinishLine = finishLine,
				FinishPosition = finishPosition,
				AddingTime = setAddingTime ? DateTime.Now : ExerciseCodeReview.NullAddingTime,
			});

			await db.SaveChangesAsync();

			return await db.ExerciseCodeReviews.FirstOrDefaultAsync(r => r.Id == review.Entity.Id);
		}

		public Task<ExerciseCodeReview> AddExerciseCodeReview(ManualExerciseChecking checking, string userId, int startLine, int startPosition, int finishLine, int finishPosition, string comment, bool setAddingTime = true)
		{
			return AddExerciseCodeReview(null, checking, userId, startLine, startPosition, finishLine, finishPosition, comment, setAddingTime);
		}

		public Task<ExerciseCodeReview> AddExerciseCodeReview(int? submissionId, string userId, int startLine, int startPosition, int finishLine, int finishPosition, string comment, bool setAddingTime = false)
		{
			return AddExerciseCodeReview(submissionId, null, userId, startLine, startPosition, finishLine, finishPosition, comment, setAddingTime);
		}

		public async Task<ExerciseCodeReview> FindExerciseCodeReviewById(int reviewId)
		{
			return await db.ExerciseCodeReviews.FindAsync(reviewId);
		}

		public async Task DeleteExerciseCodeReview(ExerciseCodeReview review)
		{
			review.IsDeleted = true;
			await db.SaveChangesAsync();
		}

		public async Task UpdateExerciseCodeReview(ExerciseCodeReview review, string newComment)
		{
			review.Comment = newComment;
			await db.SaveChangesAsync();
		}

		public async Task<Dictionary<int, List<ExerciseCodeReview>>> GetExerciseCodeReviewForCheckings(IEnumerable<int> checkingsIds)
		{
			return (await db.ExerciseCodeReviews
					.Where(r => r.ExerciseCheckingId.HasValue && checkingsIds.Contains(r.ExerciseCheckingId.Value) && !r.IsDeleted)
					.ToListAsync())
				.GroupBy(r => r.ExerciseCheckingId)
				.ToDictionary(g => g.Key.Value, g => g.ToList());
		}

		public async Task<List<string>> GetTopUserReviewComments(string courseId, Guid slideId, string userId, int count)
		{
			var result = await db.ExerciseCodeReviews.Include(r => r.ExerciseChecking)
				.Where(r => r.ExerciseChecking.CourseId == courseId &&
							r.ExerciseChecking.SlideId == slideId &&
							r.AuthorId == userId &&
							!r.HiddenFromTopComments &&
							!r.IsDeleted)
				.Select(r => new { r.Comment, r.ExerciseChecking.Timestamp })
				.GroupBy(r => r.Comment)
				.Select(g => new
				{
					g.Key,
					Count = g.Count(),
					Timestamp = g.Max(r => r.Timestamp)
				})
				.OrderByDescending(g => g.Count)
				.ThenByDescending(g => g.Timestamp)
				.Take(count)
				.Select(g => g.Key)
				.ToListAsync();
			return result;
		}

		public async Task<List<string>> GetTopOtherUsersReviewComments(string courseId, Guid slideId, string userId, int count, List<string> excludeComments)
		{
			var excludeCommentsSet = excludeComments.ToHashSet();
			var result = (await db.ExerciseCodeReviews.Include(r => r.ExerciseChecking)
					.Where(r => r.ExerciseChecking.CourseId == courseId &&
								r.ExerciseChecking.SlideId == slideId &&
								r.AuthorId != userId &&
								!r.HiddenFromTopComments &&
								!r.IsDeleted)
					.GroupBy(r => r.Comment)
					.Select(g => new { g.Key, Count = g.Count() })
					.OrderByDescending(g => g.Count)
					.Take(count * 2)
					.Select(g => g.Key)
					.ToListAsync())
				.Where(c => !excludeCommentsSet.Contains(c))
				.Take(count)
				.ToList();
			return result;
		}

		public async Task<Dictionary<string, List<Guid>>> GetSlideIdsWaitingForManualExerciseCheckAsync(string courseId, IEnumerable<string> userIds)
		{
			return (await db.ManualExerciseCheckings
					.Where(c => c.CourseId == courseId && userIds.Contains(c.UserId) && !c.IsChecked)
					.Select(c => new { c.UserId, c.SlideId })
					.Distinct()
					.ToListAsync())
				.GroupBy(c => c.UserId)
				.ToDictionary(g => g.Key, g => g.Select(c => c.SlideId).ToList());
		}

		public async Task<Dictionary<string, List<Guid>>> GetProhibitFurtherManualCheckingSlides(string courseId, IEnumerable<string> userIds)
		{
			return (await db.ManualExerciseCheckings
					.Where(c => c.CourseId == courseId && userIds.Contains(c.UserId) && c.ProhibitFurtherManualCheckings)
					.Select(c => new { c.UserId, c.SlideId })
					.Distinct()
					.ToListAsync())
				.GroupBy(c => c.UserId)
				.ToDictionary(g => g.Key, g => g.Select(c => c.SlideId).ToList());
		}

		public async Task HideFromTopCodeReviewComments(string courseId, Guid slideId, string userId, string comment)
		{
			var reviews = await db.ExerciseCodeReviews.Include(r => r.ExerciseChecking)
				.Where(r => r.ExerciseChecking.CourseId == courseId
							&& r.ExerciseChecking.SlideId == slideId
							&& r.AuthorId == userId
							&& r.Comment == comment
							&& !r.IsDeleted)
				.ToListAsync();

			foreach (var review in reviews)
				review.HiddenFromTopComments = true;
			await db.SaveChangesAsync();
		}

		public async Task<List<ExerciseCodeReview>> GetLastYearReviewComments(string courseId, Guid slideId)
		{
			var lastYear = DateTime.Today.AddYears(-1);
			var result = await db.ExerciseCodeReviews.Where(
				r => r.ExerciseChecking.CourseId == courseId &&
					r.ExerciseChecking.SlideId == slideId &&
					!r.IsDeleted &&
					r.ExerciseChecking.Timestamp > lastYear
			).ToListAsync();
			return result;
		}

		public async Task<ExerciseCodeReviewComment> AddExerciseCodeReviewComment(string authorId, int reviewId, string text)
		{
			var codeReviewComment = new ExerciseCodeReviewComment
			{
				AuthorId = authorId,
				ReviewId = reviewId,
				Text = text,
				IsDeleted = false,
				AddingTime = DateTime.Now,
			};

			db.ExerciseCodeReviewComments.Add(codeReviewComment);
			await db.SaveChangesAsync();

			return await db.ExerciseCodeReviewComments.FirstOrDefaultAsync(r => r.Id == codeReviewComment.Id);
		}

		public async Task<ExerciseCodeReviewComment> FindExerciseCodeReviewCommentById(int commentId)
		{
			return await db.ExerciseCodeReviewComments.FindAsync(commentId);
		}

		public async Task<List<ExerciseCodeReviewComment>> GetExerciseCodeReviewComments(string courseId, Guid slideId, string userId)
		{
			return await db.ExerciseCodeReviewComments
				.Include(c => c.Author)
				.Where(c =>
					(
						c.Review.ExerciseChecking.CourseId == courseId
						&& c.Review.ExerciseChecking.SlideId == slideId
						&& c.Review.ExerciseChecking.UserId == userId
						&& !c.Review.IsDeleted
						||
						c.Review.Submission.CourseId == courseId
						&& c.Review.Submission.SlideId == slideId
						&& c.Review.Submission.UserId == userId
					) && !c.IsDeleted
				)
				.ToListAsync();
		}

		public async Task DeleteExerciseCodeReviewComment(ExerciseCodeReviewComment comment)
		{
			comment.IsDeleted = true;
			await db.SaveChangesAsync();
		}

		public async Task<DateTime?> GetExerciseLastRightAnswerDate(string courseId, Guid slideId)
		{
			return await db.AutomaticExerciseCheckings
				.Where(c => c.CourseId == courseId && c.SlideId == slideId)
				.Select(c => (DateTime?)c.Timestamp)
				.OrderByDescending(t => t)
				.FirstOrDefaultAsync();
		}

		public async Task<int> GetExerciseUsersCount(string courseId, Guid slideId)
		{
			return await db.AutomaticExerciseCheckings
				.Where(c => c.CourseId == courseId && c.SlideId == slideId)
				.Select(c => c.UserId)
				.Distinct()
				.CountAsync();
		}

		public async Task<int> GetExerciseUsersWithRightAnswerCount(string courseId, Guid slideId)
		{
			return await db.AutomaticExerciseCheckings
				.Where(c => c.CourseId == courseId && c.SlideId == slideId && c.IsRightAnswer)
				.Select(c => c.UserId)
				.Distinct()
				.CountAsync();
		}
	}
}