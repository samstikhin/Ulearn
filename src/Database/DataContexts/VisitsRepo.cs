﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Database.Models;
using Vostok.Logging.Abstractions;
using Ulearn.Core.Courses.Slides;
using Ulearn.Core.Courses.Slides.Exercises;

namespace Database.DataContexts
{
	public class VisitsRepo
	{
		private static ILog log => LogProvider.Get().ForContext(typeof(VisitsRepo));

		private readonly ULearnDb db;
		private readonly SlideCheckingsRepo slideCheckingsRepo;

		public VisitsRepo(ULearnDb db)
		{
			this.db = db;
			slideCheckingsRepo = new SlideCheckingsRepo(db);
		}

		public async Task AddVisit(string courseId, Guid slideId, string userId, string ipAddress)
		{
			await SetLastVisit(courseId, slideId, userId);
			var visit = FindVisit(courseId, slideId, userId);
			if (visit == null)
			{
				db.Visits.Add(new Visit
				{
					UserId = userId,
					CourseId = courseId,
					SlideId = slideId,
					Timestamp = DateTime.Now,
					IpAddress = ipAddress,
				});
			}
			else if (visit.IpAddress != ipAddress)
				visit.IpAddress = ipAddress;

			await db.SaveChangesAsync();
		}

		private async Task SetLastVisit(string courseId, Guid slideId, string userId)
		{
			var lastVisit = FindLastVisit(courseId, userId, slideId);
			if (lastVisit == null)
			{
				db.LastVisits.Add(new LastVisit
				{
					UserId = userId,
					CourseId = courseId,
					SlideId = slideId,
					Timestamp = DateTime.Now,
				});
			}
			else
			{
				lastVisit.Timestamp = DateTime.Now;
			}
		}

		public Visit FindVisit(string courseId, Guid slideId, string userId)
		{
			return db.Visits.FirstOrDefault(v => v.CourseId == courseId && v.SlideId == slideId && v.UserId == userId);
		}

		public  LastVisit FindLastVisit(string courseId, string userId, Guid? slideId = null)
		{
			if (slideId == null)
				return db.LastVisits
					.Where(v => v.CourseId == courseId && v.UserId == userId)	
					.OrderByDescending(v => v.Timestamp)
					.FirstOrDefault();
			return db.LastVisits
				.FirstOrDefault(v => v.CourseId == courseId && v.UserId == userId && slideId == v.SlideId);
		}

		public HashSet<Guid> GetIdOfVisitedSlides(string courseId, string userId)
		{
			return new HashSet<Guid>(db.Visits.Where(v => v.UserId == userId && v.CourseId == courseId).Select(x => x.SlideId));
		}

		public Task UpdateScoreForVisit(string courseId, Slide slide, string userId)
		{
			var maxSlideScore = slide.MaxScore;
			var newScore = slide is ExerciseSlide ex
				? slideCheckingsRepo.GetExerciseSlideScoreAndPercent(courseId, ex, userId).Score
				: slideCheckingsRepo.GetUserScoreForQuizSlide(courseId, slide.Id, userId);
			newScore = Math.Min(newScore, maxSlideScore);
			var isPassed = slideCheckingsRepo.IsSlidePassed(courseId, slide.Id, userId);
			if (IsSkipped(courseId, slide.Id, userId))
				newScore = 0;
			log.Info($"Обновляю количество баллов пользователя {userId} за слайд {slide.Id} в курсе \"{courseId}\". " +
					$"Новое количество баллов: {newScore}, слайд пройден: {isPassed}");
			return UpdateAttempts(courseId, slide.Id, userId, visit =>
			{
				visit.Score = newScore;
				visit.IsPassed = isPassed;
			});
		}

		private async Task UpdateAttempts(string courseId, Guid slideId, string userId, Action<Visit> action)
		{
			var visit = FindVisit(courseId, slideId, userId);
			if (visit == null)
			{
				await AddVisit(courseId, slideId, userId, null);
				visit = FindVisit(courseId, slideId, userId);
			}

			action(visit);
			await db.SaveChangesAsync().ConfigureAwait(false);
		}

		public Task RemoveAttempts(string courseId, Guid slideId, string userId)
		{
			return UpdateAttempts(courseId, slideId, userId, visit =>
			{
				visit.AttemptsCount = 0;
				visit.Score = 0;
				visit.IsPassed = false;
			});
		}

		public Dictionary<Guid, int> GetScoresForSlides(string courseId, string userId, IEnumerable<Guid> slidesIds = null)
		{
			var visits = db.Visits.Where(v => v.CourseId == courseId && v.UserId == userId);
			if (slidesIds != null)
				visits = visits.Where(v => slidesIds.Contains(v.SlideId));
			return visits
				.GroupBy(v => v.SlideId, (s, v) => new { Key = s, Value = v.FirstOrDefault() })
				.ToDictionary(g => g.Key, g => g.Value.Score);
		}

		public List<Guid> GetSlidesWithUsersManualChecking(string courseId, string userId)
		{
			return db.Visits.Where(v => v.CourseId == courseId && v.UserId == userId)
				.Where(v => v.HasManualChecking)
				.Select(v => v.SlideId)
				.ToList();
		}

		public bool HasManualChecking(string courseId, string userId, Guid slideId)
		{
			return db.Visits.Any(v => v.CourseId == courseId && v.UserId == userId && v.SlideId == slideId && v.HasManualChecking);
		}

		public Task MarkVisitsAsWithManualChecking(string courseId, Guid slideId, string userId)
		{
			return UpdateAttempts(courseId, slideId, userId, visit => { visit.HasManualChecking = true; });
		}

		public int GetScore(string courseId, Guid slideId, string userId)
		{
			return db.Visits
				.Where(v => v.CourseId == courseId && v.SlideId == slideId && v.UserId == userId)
				.Select(v => v.Score)
				.FirstOrDefault();
		}

		public Dictionary<string, int> GetScore(string courseId, Guid slideId, List<string> userIds)
		{
			var scores = db.Visits
				.Where(v => v.CourseId == courseId && v.SlideId == slideId && userIds.Contains(v.UserId))
				.Select(v => new { v.UserId, v.Score })
				.ToList()
				.GroupBy(v => v.UserId)
				.ToDictionary(v => v.Key, v => v.First().Score);
			foreach (var userId in userIds)
				if (!scores.ContainsKey(userId))
					scores[userId] = 0;
			return scores;
		}

		public async Task SkipSlide(string courseId, Guid slideId, string userId)
		{
			var visiter = FindVisit(courseId, slideId, userId);
			if (visiter != null)
				visiter.IsSkipped = true;
			else
				db.Visits.Add(new Visit
				{
					UserId = userId,
					CourseId = courseId,
					SlideId = slideId,
					Timestamp = DateTime.Now,
					IsSkipped = true
				});
			await db.SaveChangesAsync();
		}

		public bool IsSkipped(string courseId, Guid slideId, string userId)
		{
			return db.Visits.Any(v => v.CourseId == courseId && v.SlideId == slideId && v.UserId == userId && v.IsSkipped);
		}

		public bool IsPassed(string courseId, Guid slideId, string userId)
		{
			return db.Visits.Any(v => v.CourseId == courseId && v.SlideId == slideId && v.UserId == userId && v.IsPassed);
		}

		public bool IsSkippedOrPassed(string courseId, Guid slideId, string userId)
		{
			return db.Visits.Any(
				v => v.CourseId == courseId && v.SlideId == slideId && v.UserId == userId && (v.IsPassed || v.IsSkipped)
			);
		}

		public async Task AddVisits(IEnumerable<Visit> visits)
		{
			foreach (var visit in visits)
			{
				if (db.Visits.Any(x => x.UserId == visit.UserId && x.SlideId == visit.SlideId && x.CourseId == visit.CourseId))
					continue;
				db.Visits.Add(visit);
			}

			await db.SaveChangesAsync();
		}

		public IQueryable<Visit> GetVisitsInPeriod(string courseId, IEnumerable<Guid> slidesIds, DateTime periodStart, DateTime periodFinish, IEnumerable<string> usersIds = null)
		{
			var filteredVisits = db.Visits.Where(v => v.CourseId == courseId && slidesIds.Contains(v.SlideId));
			if (periodStart != DateTime.MinValue || periodFinish < DateTime.Now.AddSeconds(-10))
				filteredVisits = filteredVisits.Where(v => periodStart <= v.Timestamp && v.Timestamp <= periodFinish);
			if (usersIds != null)
				filteredVisits = filteredVisits.Where(v => usersIds.Contains(v.UserId));
			return filteredVisits;
		}

		public IQueryable<Visit> GetVisitsInPeriod(VisitsFilterOptions options)
		{
			var filteredVisits = db.Visits.Where(v => v.CourseId == options.CourseId);
			if (options.PeriodStart != DateTime.MinValue || options.PeriodFinish < DateTime.Now.AddSeconds(-10))
				filteredVisits = filteredVisits.Where(v => options.PeriodStart <= v.Timestamp && v.Timestamp <= options.PeriodFinish);
			if (options.SlidesIds != null)
				filteredVisits = filteredVisits.Where(v => options.SlidesIds.Contains(v.SlideId));
			if (options.UserIds != null)
			{
				if (options.IsUserIdsSupplement)
					filteredVisits = filteredVisits.Where(v => !options.UserIds.Contains(v.UserId));
				else
					filteredVisits = filteredVisits.Where(v => options.UserIds.Contains(v.UserId));
			}

			return filteredVisits;
		}

		public Dictionary<Guid, List<Visit>> GetVisitsInPeriodForEachSlide(VisitsFilterOptions options)
		{
			return GetVisitsInPeriod(options)
				.AsEnumerable()
				.GroupBy(v => v.SlideId)
				.ToDictionary(g => g.Key, g => g.ToList());
		}

		public IEnumerable<string> GetUsersVisitedAllSlides(VisitsFilterOptions options)
		{
			if (options.SlidesIds == null)
				throw new ArgumentNullException(nameof(options.SlidesIds));

			var slidesCount = options.SlidesIds.Count;

			return GetVisitsInPeriod(options)
				.Select(v => new { v.UserId, v.SlideId })
				.Distinct()
				.GroupBy(v => v.UserId)
				.Where(g => g.Count() == slidesCount)
				.Select(g => g.Key);
		}

		public HashSet<string> GetUserCourses(string userId)
		{
			return new HashSet<string>(db.Visits.Where(v => v.UserId == userId).Select(v => v.CourseId).Distinct());
		}

		public List<string> GetCourseUsers(string courseId)
		{
			return db.Visits.Where(v => v.CourseId == courseId).Select(v => v.UserId).Distinct().ToList();
		}

		public List<RatingEntry> GetCourseRating(string courseId, int minScore, List<Guid> requiredSlides)
		{
			var groupedVisits = db.Visits.Where(v => v.CourseId == courseId)
				.GroupBy(v => v.UserId)
				.Where(g => g.Sum(v => v.Score) >= minScore)
				.ToList();
			var userIds = groupedVisits.Select(g => g.Key).ToList();

			List<string> usersWithAllRequiredSlides;
			if (requiredSlides.Count > 0)
			{
				usersWithAllRequiredSlides = db.Visits
					.Where(v => v.CourseId == courseId && requiredSlides.Contains(v.SlideId) && userIds.Contains(v.UserId))
					.GroupBy(v => v.UserId)
					.ToList()
					.Where(g => g.DistinctBy(v => v.SlideId).Count() >= requiredSlides.Count)
					.Select(g => g.Key)
					.ToList();
			}
			else
			{
				usersWithAllRequiredSlides = userIds;
			}

			return groupedVisits
				.Where(g => usersWithAllRequiredSlides.Contains(g.Key))
				.Select(g => new RatingEntry(g.First().User, g.Sum(v => v.Score), g.Max(v => v.Timestamp)))
				.OrderByDescending(r => r.Score)
				.ToList();
		}
	}

	public class RatingEntry
	{
		public RatingEntry(ApplicationUser user, int score, DateTime lastVisitTime)
		{
			User = user;
			Score = score;
			LastVisitTime = lastVisitTime;
		}

		public readonly ApplicationUser User;
		public readonly int Score;
		public readonly DateTime LastVisitTime;
	}
}