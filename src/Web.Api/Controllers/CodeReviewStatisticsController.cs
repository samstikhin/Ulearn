﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Database;
using Database.Models;
using Database.Repos;
using Database.Repos.Groups;
using Database.Repos.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Ulearn.Core.Courses.Slides.Exercises;
using Ulearn.Web.Api.Authorization;
using Ulearn.Web.Api.Models.Responses.CodeReviewStatistics;

namespace Ulearn.Web.Api.Controllers
{
	[Route("/codereview-statistics")]
	public class CodeReviewStatisticsController : BaseController
	{
		private readonly ISlideCheckingsRepo slideCheckingsRepo;
		private readonly IGroupsRepo groupsRepo;
		private readonly IGroupMembersRepo groupMembersRepo;
		private readonly ICourseRolesRepo courseRolesRepo;

		public CodeReviewStatisticsController(IWebCourseManager courseManager,
			ISlideCheckingsRepo slideCheckingsRepo,
			IUsersRepo usersRepo,
			IGroupsRepo groupsRepo,
			IGroupMembersRepo groupMembersRepo,
			ICourseRolesRepo courseRolesRepo,
			UlearnDb db)
			: base(courseManager, db, usersRepo)
		{
			this.slideCheckingsRepo = slideCheckingsRepo;
			this.groupsRepo = groupsRepo;
			this.groupMembersRepo = groupMembersRepo;
			this.courseRolesRepo = courseRolesRepo;
		}

		/// <summary>
		/// Статистика по выполненным ревью для каждого преподавателя в курсе
		/// </summary>
		[HttpGet]
		[CourseAccessAuthorize(CourseAccessType.ApiViewCodeReviewStatistics)]
		public async Task<ActionResult<CodeReviewInstructorsStatisticsResponse>> InstructorsStatistics([FromQuery(Name = "course_id")] [BindRequired]
			string courseId, int count = 10000, DateTime? from = null, DateTime? to = null)
		{
			var course = await courseManager.FindCourseAsync(courseId);
			if (course == null)
				return NotFound();

			if (!from.HasValue)
				from = DateTime.MinValue;
			if (!to.HasValue)
				to = DateTime.MaxValue;

			count = Math.Min(count, 10000);

			var instructorIds = await courseRolesRepo.GetListOfUsersWithCourseRoleAsync(CourseRoleType.Instructor, course.Id, false).ConfigureAwait(false);
			var instructors = await usersRepo.GetUsersByIdsAsync(instructorIds).ConfigureAwait(false);

			var exerciseSlides = course.GetSlides(true).OfType<ExerciseSlide>().ToList();

			var allSlideCheckings = (await slideCheckingsRepo.GetManualCheckingQueue<ManualExerciseChecking>(new ManualCheckingQueueFilterOptions
			{
				CourseId = course.Id,
				Count = count,
				OnlyChecked = null,
				From = @from.Value,
				To = to.Value,
			}).ConfigureAwait(false)).ToList();

			var result = new CodeReviewInstructorsStatisticsResponse
			{
				AnalyzedCodeReviewsCount = allSlideCheckings.Count,
				Instructors = new List<CodeReviewInstructorStatistics>()
			};
			foreach (var instructor in instructors)
			{
				var checkingsCheckedByInstructor = allSlideCheckings.Where(c => c.IsChecked && (c.LockedById == instructor.Id || c.Reviews.Any(r => r.AuthorId == instructor.Id))).ToList();
				var instructorGroups = await groupsRepo.GetMyGroupsFilterAccessibleToUserAsync(course.Id, instructor.Id).ConfigureAwait(false);
				var instructorGroupMemberIds = (await groupMembersRepo.GetGroupsMembersAsync(instructorGroups.Select(g => g.Id).ToList()).ConfigureAwait(false)).Select(m => m.UserId);
				var checkingQueue = allSlideCheckings.Where(c => !c.IsChecked && instructorGroupMemberIds.Contains(c.UserId)).ToList();
				var comments = checkingsCheckedByInstructor.SelectMany(c => c.NotDeletedReviews).ToList();
				var instructorStatistics = new CodeReviewInstructorStatistics
				{
					Instructor = BuildShortUserInfo(instructor, discloseLogin: true),
					Exercises = exerciseSlides.Select(
							slide => new CodeReviewExerciseStatistics
							{
								SlideId = slide.Id,
								ReviewedSubmissionsCount = checkingsCheckedByInstructor.Where(c => c.SlideId == slide.Id).DistinctBy(c => c.SubmissionId).Count(),
								QueueSize = checkingQueue.Count(c => c.SlideId == slide.Id),
								CommentsCount = comments.Count(c => c.ExerciseChecking.SlideId == slide.Id),
							}
						)
						.Where(s => s.ReviewedSubmissionsCount + s.QueueSize + s.CommentsCount > 0) // Ignore empty (zeros) records
						.ToList()
				};
				result.Instructors.Add(instructorStatistics);
			}

			return result;
		}
	}
}