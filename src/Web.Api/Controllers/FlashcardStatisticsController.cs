using Database;
using Database.Repos.Users;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Database.Models;
using Database.Repos;
using Database.Repos.Flashcards;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Ulearn.Core.Courses;
using Ulearn.Web.Api.Models.Responses.Flashcards;

namespace Ulearn.Web.Api.Controllers
{
	[Route("/flashcard-statistics")]
	public class FlashcardStatisticsController : BaseController
	{
		private ICourseRolesRepo courseRolesRepo;
		private IUsersFlashcardsVisitsRepo usersFlashcardsVisitsRepo;

		public FlashcardStatisticsController(IWebCourseManager courseManager, UlearnDb db, IUsersRepo usersRepo, ICourseRolesRepo courseRolesRepo, IUsersFlashcardsVisitsRepo usersFlashcardsVisitsRepo)
			: base(courseManager, db, usersRepo)
		{
			this.courseRolesRepo = courseRolesRepo;
			this.usersFlashcardsVisitsRepo = usersFlashcardsVisitsRepo;
		}

		/// <summary>
		/// Статистика по всем флеш-картам в курсе
		/// </summary>
		/// <returns></returns>
		[HttpGet]
		public async Task<ActionResult<FlashcardsStatistics>> FlashcardsStatistics([FromQuery(Name = "course_id")] [BindRequired]
			string courseId)
		{
			var course = await courseManager.FindCourseAsync(courseId);
			if (course == null)
				return NotFound();
			var hasUserAccessToCourse = await courseRolesRepo.HasUserAccessToCourseAsync(UserId, course.Id, CourseRoleType.Instructor);
			if (!hasUserAccessToCourse)
			{
				return BadRequest($"You don't have access to course with id {course.Id}");
			}

			var flashcardVisitsByCourse = await usersFlashcardsVisitsRepo.GetUserFlashcardsVisitsAsync(course.Id);
			var statistics = ToFlashcardsStatistics(flashcardVisitsByCourse, course);

			return statistics;
		}

		private FlashcardsStatistics ToFlashcardsStatistics(List<UserFlashcardsVisit> userFlashcardsVisits, Course course)
		{
			var result = new FlashcardsStatistics();

			var visitsGroupedByFlashcard = userFlashcardsVisits.GroupBy(x => x.FlashcardId).ToDictionary(x => x.Key);
			
			foreach (var unit in course.GetUnitsNotSafe())
			{
				var flashcards = unit.Flashcards;
				foreach (var flashcard in flashcards)
				{
					var flashcardStat = new FlashcardStatistic { FlashcardId = flashcard.Id, UnitId = unit.Id, UnitTitle = unit.Title };

					if (visitsGroupedByFlashcard.TryGetValue(flashcard.Id, out var group))
					{
						var uniqueUsers = new HashSet<string>();
						foreach (var e in group)
						{
							uniqueUsers.Add(e.UserId);
							flashcardStat.Statistics.Add(e.Rate);
						}

						flashcardStat.UniqueVisitCount = uniqueUsers.Count;
						flashcardStat.VisitCount = group.Count();
					}

					result.Statistics.Add(flashcardStat);
				}
			}

			return result;
		}
	}
}