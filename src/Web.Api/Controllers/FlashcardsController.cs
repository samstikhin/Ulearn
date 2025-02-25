using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Database;
using Database.Models;
using Database.Repos;
using Database.Repos.Flashcards;
using Database.Repos.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ulearn.Core.Courses;
using Ulearn.Core.Courses.Slides;
using Ulearn.Core.Courses.Slides.Flashcards;
using Ulearn.Core.Courses.Units;
using Ulearn.Web.Api.Models.Responses.Flashcards;

namespace Ulearn.Web.Api.Controllers
{
	[Route("/courses")]
	public class FlashcardsController : BaseController
	{
		private readonly IUsersFlashcardsVisitsRepo usersFlashcardsVisitsRepo;
		private readonly IUserFlashcardsUnlockingRepo userFlashcardsUnlockingRepo;
		private readonly IUnitsRepo unitsRepo;


		public FlashcardsController(IWebCourseManager courseManager, UlearnDb db, IUsersRepo usersRepo,
			IUsersFlashcardsVisitsRepo usersFlashcardsVisitsRepo,
			IUserFlashcardsUnlockingRepo userFlashcardsUnlockingRepo,
			IUnitsRepo unitsRepo)
			: base(courseManager, db, usersRepo)
		{
			this.usersFlashcardsVisitsRepo = usersFlashcardsVisitsRepo;
			this.userFlashcardsUnlockingRepo = userFlashcardsUnlockingRepo;
			this.unitsRepo = unitsRepo;
		}

		/// <summary>
		/// Коллекция объектов флешкарт с оценками, сгруппированных по модулям по курсу
		/// </summary>
		/// <param name="course"></param>
		/// <returns></returns>
		[HttpGet("{courseId}/flashcards-by-units")]
		public async Task<ActionResult<FlashcardResponseByUnits>> Flashcards([FromRoute] Course course)
		{
			var userFlashcardsVisitsByCourse = await usersFlashcardsVisitsRepo.GetUserFlashcardsVisitsAsync(UserId, course.Id);
			var flashcardResponseByUnits = new FlashcardResponseByUnits();
			var visibleUnits = await unitsRepo.GetVisibleUnitIdsAsync(course, UserId);
			foreach (var unit in course.GetUnits(visibleUnits))
			{
				var unitFlashcardsResponse = new UnitFlashcardsResponse();
				var unitFlashcards = unit.Flashcards;
				if (unitFlashcards.Count == 0)
					continue;

				var flashcardResponsesEnumerable = GetFlashcardResponses(unit, unitFlashcards, userFlashcardsVisitsByCourse);

				unitFlashcardsResponse.Flashcards.AddRange(flashcardResponsesEnumerable);
				unitFlashcardsResponse.UnitId = unit.Id;
				unitFlashcardsResponse.UnitTitle = unit.Title;
				unitFlashcardsResponse.Unlocked = await IsUnlocked(course, unit);
				flashcardResponseByUnits.Units.Add(unitFlashcardsResponse);
			}

			return flashcardResponseByUnits;
		}

		private async Task<bool> IsUnlocked(Course course, Unit unit)
		{
			var unlocking = await userFlashcardsUnlockingRepo.GetUserFlashcardsUnlocking(UserId, course, unit);
			if (unlocking != null)
				return true;
			var flashcards = unit.Flashcards;
			var userVisits = await usersFlashcardsVisitsRepo.GetUserFlashcardsVisitsAsync(UserId, course.Id, unit.Id);
			var userVisitsDict = new Dictionary<string, UserFlashcardsVisit>();
			foreach (var visit in userVisits)
			{
				userVisitsDict[visit.FlashcardId] = visit;
			}

			if (!flashcards.All(x => userVisitsDict.ContainsKey(x.Id)))
				return false;
			await userFlashcardsUnlockingRepo.AddUserFlashcardsUnlocking(UserId, course, unit);
			return true;
		}

		private IEnumerable<FlashcardResponse> GetFlashcardResponses(Unit unit, List<Flashcard> flashcards, List<UserFlashcardsVisit> userFlashcardsVisits)
		{
			var userFlashcardsVisitsDictionary = GetFlashcardsUsersVisitsDictionaryIncludingNotRated(userFlashcardsVisits, flashcards);
			var lastRateIndexes = GetFlashcardsLastRateIndexes(flashcards, userFlashcardsVisits);

			foreach (var flashcard in flashcards)
			{
				var question = GetRenderedContent(flashcard.Question.Blocks);
				var answer = GetRenderedContent(flashcard.Answer.Blocks);

				var rateResponse = userFlashcardsVisitsDictionary.TryGetValue(flashcard.Id, out var visit) ? visit.Rate : Rate.NotRated;

				var unitIdResponse = unit.Id;
				var lastRateIndex = lastRateIndexes[flashcard.Id];

				var flashcardResponse = new FlashcardResponse
				{
					Answer = answer,
					Question = question,
					Rate = rateResponse,
					Id = flashcard.Id,
					UnitId = unitIdResponse,
					UnitTitle = unit.Title,
					TheorySlidesIds = flashcard.TheorySlidesIds,
					LastRateIndex = lastRateIndex
				};
				yield return flashcardResponse;
			}
		}

		private Dictionary<string, int> GetFlashcardsLastRateIndexes(List<Flashcard> flashcards, List<UserFlashcardsVisit> userFlashcardsVisits)
		{
			userFlashcardsVisits = userFlashcardsVisits.ToList();

			var result = new Dictionary<string, int>();

			foreach (var flashcard in flashcards)
			{
				result[flashcard.Id] = 0;
			}

			for (var i = 0; i < userFlashcardsVisits.Count; i++)
			{
				var visit = userFlashcardsVisits[i];
				result[visit.FlashcardId] = i + 1;
			}

			return result;
		}

		private static string GetRenderedContent(SlideBlock[] blocks)
		{
			var content = new StringBuilder();
			foreach (var block in blocks)
				content.Append(Flashcard.RenderBlock(block));
			return content.ToString();
		}

		private Dictionary<string, UserFlashcardsVisit> GetFlashcardsUsersVisitsDictionaryIncludingNotRated(List<UserFlashcardsVisit> userFlashcardsVisits, List<Flashcard> flashcards)
		{
			var result = new Dictionary<string, UserFlashcardsVisit>();
			foreach (var userFlashcardsVisit in userFlashcardsVisits)
			{
				result[userFlashcardsVisit.FlashcardId] = userFlashcardsVisit;
			}

			foreach (var flashcard in flashcards)
			{
				if (!result.ContainsKey(flashcard.Id))
				{
					result[flashcard.Id] = new UserFlashcardsVisit { FlashcardId = flashcard.Id, Rate = Rate.NotRated };
				}
			}

			return result;
		}

		/// <summary>
		/// Изменить оценку для флеш-карты
		/// </summary>
		///
		[Authorize]
		[HttpPut("{courseId}/flashcards/{flashcardId}/status")]
		[ProducesResponseType((int)HttpStatusCode.NoContent)]
		public async Task<IActionResult> Status([FromRoute] Course course, [FromRoute] string flashcardId, [FromBody] Rate rate)
		{
			var visibleUnitsIds = await unitsRepo.GetVisibleUnitIdsAsync(course, UserId);
			var unit = course.GetUnits(visibleUnitsIds).FirstOrDefault(x => x.GetFlashcardById(flashcardId) != null);
			if (unit is null)
				return BadRequest($"flashcard with id {flashcardId} does not exist");
			await usersFlashcardsVisitsRepo.AddFlashcardVisitAsync(UserId, course.Id, unit.Id, flashcardId, rate, DateTime.Now);
			return NoContent();
		}
	}
}