using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Database.Models;
using Database.Models.Quizzes;
using JetBrains.Annotations;
using Ulearn.Core.Courses;
using Ulearn.Core.Courses.Slides.Exercises;

namespace Database.Repos
{
	public interface ISlideCheckingsRepo
	{
		Task<ManualQuizChecking> AddManualQuizChecking(UserQuizSubmission submission, string courseId, Guid slideId, string userId);
		Task<AutomaticQuizChecking> AddAutomaticQuizChecking(UserQuizSubmission submission, string courseId, Guid slideId, string userId, int automaticScore);
		Task<bool> HasManualExerciseChecking(string courseId, Guid slideId, string userId, int submissionId);
		Task<ManualExerciseChecking> AddManualExerciseChecking(string courseId, Guid slideId, string userId, int submissionId);
		Task<Dictionary<string, List<Guid>>> GetSlideIdsWaitingForManualExerciseCheckAsync(string courseId, IEnumerable<string> userIds);
		Task<Dictionary<string, List<Guid>>> GetProhibitFurtherManualCheckingSlides(string courseId, IEnumerable<string> userIds);
		Task RemoveWaitingManualCheckings<T>(string courseId, Guid slideId, string userId, bool startTransaction = true) where T : AbstractManualSlideChecking;
		Task RemoveAttempts(string courseId, Guid slideId, string userId, bool saveChanges = true);
		Task<bool> IsSlidePassed(string courseId, Guid slideId, string userId);
		Task<(int Score, int? Percent)> GetExerciseSlideScoreAndPercent(string courseId, ExerciseSlide slide, string userId);
		Task<int?> GetUserReviewPercentForExerciseSlide(string courseId, ExerciseSlide slide, string userId, DateTime? submissionBefore = null);
		Task<List<(Guid SlideId, int Score, int Percent)>> GetPassedManualExerciseCheckingsScoresAndPercents(Course course, string userId);
		Task<int> GetUserScoreForQuizSlide(string courseId, Guid slideId, string userId);
		Task<List<T>> GetManualCheckingQueue<T>(ManualCheckingQueueFilterOptions options) where T : AbstractManualSlideChecking;
		Task<int> GetQuizManualCheckingCount(string courseId, Guid slideId, string userId, DateTime? beforeTimestamp);
		Task<HashSet<Guid>> GetManualCheckingQueueSlideIds<T>(ManualCheckingQueueFilterOptions options) where T : AbstractManualSlideChecking;
		Task<T> FindManualCheckingById<T>(int id) where T : AbstractManualSlideChecking;
		Task<bool> IsProhibitedToSendExerciseToManualChecking(string courseId, Guid slideId, string userId);
		Task LockManualChecking<T>(T checkingItem, string lockedById) where T : AbstractManualSlideChecking;
		Task MarkManualQuizCheckingAsChecked(ManualQuizChecking queueItem, int score);
		Task MarkManualExerciseCheckingAsChecked(ManualExerciseChecking queueItem, int percent);
		Task MarkManualExerciseCheckingAsCheckedBeforeThis(ManualExerciseChecking queueItem);
		Task ProhibitFurtherExerciseManualChecking(string courseId, string userId, Guid slideId);
		Task ResetManualCheckingLimitsForUser(string courseId, string userId);
		Task ResetAutomaticCheckingLimitsForUser(string courseId, string userId);
		Task DisableProhibitFurtherManualCheckings(string courseId, string userId, Guid? slideId = null);
		Task NotCountOldAttemptsToQuizzesWithManualChecking(string courseId, string userId);
		Task NotCountOldAttemptsToQuizzesWithAutomaticChecking(string courseId, string userId);
		Task<ExerciseCodeReview> AddExerciseCodeReview(ManualExerciseChecking checking, string userId, int startLine, int startPosition, int finishLine, int finishPosition, string comment, bool setAddingTime = true);
		Task<ExerciseCodeReview> AddExerciseCodeReview([CanBeNull] int? submissionId, string userId, int startLine, int startPosition, int finishLine, int finishPosition, string comment, bool setAddingTime = false);
		Task<ExerciseCodeReview> FindExerciseCodeReviewById(int reviewId);
		Task DeleteExerciseCodeReview(ExerciseCodeReview review);
		Task UpdateExerciseCodeReview(ExerciseCodeReview review, string newComment);
		Task<Dictionary<int, List<ExerciseCodeReview>>> GetExerciseCodeReviewForCheckings(IEnumerable<int> checkingsIds);
		Task<List<string>> GetTopOtherUsersReviewComments(string courseId, Guid slideId, string userId, int count, List<string> excludeComments);
		Task HideFromTopCodeReviewComments(string courseId, Guid slideId, string userId, string comment);
		Task<List<ExerciseCodeReview>> GetLastYearReviewComments(string courseId, Guid slideId);
		Task<ExerciseCodeReviewComment> AddExerciseCodeReviewComment(string authorId, int reviewId, string text);
		Task<ExerciseCodeReviewComment> FindExerciseCodeReviewCommentById(int commentId);
		Task<List<ExerciseCodeReviewComment>> GetExerciseCodeReviewComments(string courseId, Guid slideId, string userId);
		Task DeleteExerciseCodeReviewComment(ExerciseCodeReviewComment comment);
		Task<DateTime?> GetExerciseLastRightAnswerDate(string courseId, Guid slideId);
		Task<int> GetExerciseUsersCount(string courseId, Guid slideId);
		Task<int> GetExerciseUsersWithRightAnswerCount(string courseId, Guid slideId);
	}
}