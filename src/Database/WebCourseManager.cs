﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Database.DataContexts;
using Database.Models;
using JetBrains.Annotations;
using Vostok.Logging.Abstractions;
using Ulearn.Core;
using Ulearn.Core.Courses;

namespace Database
{
	public class WebCourseManager : CourseManager
	{
		private static ILog log => LogProvider.Get().ForContext(typeof(WebCourseManager));
		public static readonly WebCourseManager Instance = new WebCourseManager();

		private readonly Dictionary<string, Guid> loadedCourseVersions = new Dictionary<string, Guid>();
		private readonly ConcurrentDictionary<string, DateTime> courseVersionFetchTime = new ConcurrentDictionary<string, DateTime>();
		private readonly TimeSpan fetchCourseVersionEvery = TimeSpan.FromMinutes(1);
		private readonly ConcurrentDictionary<string, DateTime> tempCourseUpdateTime = new ConcurrentDictionary<string, DateTime>();
		private readonly TimeSpan tempCourseUpdateEvery = TimeSpan.FromSeconds(1);
		private long allTempCoursesUpdateTime;
		private readonly TimeSpan allTempCoursesUpdateEvery = TimeSpan.FromMinutes(1);

		private List<TempCourse> tempCoursesCache;
		private readonly TimeSpan tempCoursesCacheUpdateEvery = TimeSpan.FromSeconds(0.5);
		private long tempCoursesCacheUpdateTime;

		private WebCourseManager()
			: base(GetCoursesDirectory())
		{
		}

		private readonly object @lock = new object();

		public override Course GetCourse(string courseId)
		{
			Course course;
			try
			{
				LoadCoursesIfNotYet();
				TryReloadTempCourseIfNecessary(courseId);
				course = base.GetCourse(courseId);
			}
			catch (Exception e) when (e is KeyNotFoundException || e is CourseNotFoundException || e is CourseLoadingException)
			{
				course = null;
			}
			catch (AggregateException e)
			{
				var ie = e.InnerException;
				if (ie is KeyNotFoundException || ie is CourseNotFoundException || ie is CourseLoadingException)
					course = null;
				else
					throw;
			}

			if (IsCourseVersionWasUpdatedRecent(courseId) || CourseIsBroken(courseId))
				return course ?? throw new KeyNotFoundException($"Key {courseId} not found");

			courseVersionFetchTime[courseId] = DateTime.Now;
			var coursesRepo = new CoursesRepo();
			var publishedVersion = coursesRepo.GetPublishedCourseVersion(courseId);

			if (publishedVersion == null)
				return course ?? throw new KeyNotFoundException($"Key {courseId} not found");

			lock (@lock)
			{
				if (loadedCourseVersions.TryGetValue(courseId.ToLower(), out var loadedVersionId)
					&& loadedVersionId != publishedVersion.Id)
				{
					log.Info($"Загруженная версия курса {courseId} отличается от актуальной ({loadedVersionId.ToString()} != {publishedVersion.Id}). Обновляю курс.");
					if (TryReloadCourse(courseId))
						course = base.GetCourse(courseId);
				}

				loadedCourseVersions[courseId.ToLower()] = publishedVersion.Id;
			}

			return course ?? throw new KeyNotFoundException($"Key {courseId} not found");
		}

		private bool IsCourseVersionWasUpdatedRecent(string courseId)
		{
			if (courseVersionFetchTime.TryGetValue(courseId, out var lastFetchTime))
				return lastFetchTime > DateTime.Now.Subtract(fetchCourseVersionEvery);
			return false;
		}

		public void UpdateCourseVersion(string courseId, Guid versionId)
		{
			lock (@lock)
			{
				loadedCourseVersions[courseId.ToLower()] = versionId;
			}
		}

		protected override void LoadCourseZipsToDiskFromExternalStorage(IEnumerable<string> existingOnDiskCourseIds)
		{
			log.Info($"Загружаю курсы из БД");
			var coursesRepo = new CoursesRepo();
			var files = coursesRepo.GetCourseFiles(existingOnDiskCourseIds);
			foreach (var zipFile in files)
			{
				try
				{
					var stagingCourseFile = GetStagingCourseFile(zipFile.CourseId);
					File.WriteAllBytes(stagingCourseFile.FullName, zipFile.File);
					var versionCourseFile = GetCourseVersionFile(zipFile.CourseVersionId);
					if (!versionCourseFile.Exists)
						File.WriteAllBytes(versionCourseFile.FullName, zipFile.File);
				}
				catch (Exception ex)
				{
					log.Error(ex, $"Не смог загрузить {zipFile.CourseId} из базы данных");
				}
			}
		}

		public override IEnumerable<Course> GetCourses()
		{
			try
			{
				LoadCoursesIfNotYet();
				TryCheckTempCoursesAndReloadIfNecessary();
			}
			catch (Exception e)
			{
				log.Error(e, "GetCourses exception");
			}
			return base.GetCourses();
		}

		private void TryReloadTempCourseIfNecessary([NotNull]string courseId)
		{
			try
			{
				if (CourseIsBroken(courseId))
					return;
				if (IsTempCourseUpdatedRecent(courseId))
					return;

				var tempCourses = GetTempCoursesWithCache();
				var tempCourse = tempCourses.FirstOrDefault(tc => tc.CourseId == courseId);
				if (tempCourse == null)
					return;
				Course course = null;
				try
				{
					course = base.GetCourse(courseId); // Не используется FindCourse, иначе бесконечная рекурсия
				}
				catch (Exception ex)
				{
					log.Error(ex);
				}
				if (course == null || tempCourse.LastUpdateTime < tempCourse.LoadingTime)
				{
					TryReloadCourse(courseId);
					var tempCoursesRepo = new TempCoursesRepo();
					tempCoursesRepo.UpdateTempCourseLastUpdateTime(courseId);
					courseVersionFetchTime[courseId] = DateTime.Now;
				} else if (tempCourse.LastUpdateTime > tempCourse.LoadingTime)
					courseVersionFetchTime[courseId] = DateTime.Now;
			}
			catch (Exception ex)
			{
				log.Error(ex);
			}
		}
		
		private void TryCheckTempCoursesAndReloadIfNecessary()
		{
			try
			{
				if (new DateTime(allTempCoursesUpdateTime) > DateTime.Now.Subtract(allTempCoursesUpdateEvery))
					return;
				Interlocked.Exchange(ref allTempCoursesUpdateTime, DateTime.Now.Ticks);

				var tempCourses = GetTempCoursesWithCache();
				foreach (var tempCourse in tempCourses)
				{
					var courseId = tempCourse.CourseId;
					if (CourseIsBroken(courseId))
						continue;
					Course course = null;
					try
					{
						course = base.GetCourse(courseId); // Не используется FindCourse, иначе бесконечная рекурсия
					}
					catch (Exception ex)
					{
						log.Error(ex);
					}
					if (course == null || course.GetSlides(true).Count == 0)
					{
						TryReloadCourse(courseId);
						var tempCoursesRepo = new TempCoursesRepo();
						tempCoursesRepo.UpdateTempCourseLastUpdateTime(courseId);
						courseVersionFetchTime[courseId] = DateTime.Now;
					} else if (tempCourse.LastUpdateTime > tempCourse.LoadingTime)
						courseVersionFetchTime[courseId] = DateTime.Now;
				}
			}
			catch (Exception ex)
			{
				log.Error(ex);
			}
		}

		private List<TempCourse> GetTempCoursesWithCache()
		{
			if (tempCoursesCache != null && new DateTime(tempCoursesCacheUpdateTime) > DateTime.Now.Subtract(tempCoursesCacheUpdateEvery))
				return tempCoursesCache;
			Interlocked.Exchange(ref tempCoursesCacheUpdateTime, DateTime.Now.Ticks);

			var tempCoursesRepo = new TempCoursesRepo();
			tempCoursesCache = tempCoursesRepo.GetTempCoursesNoTracking();
			return tempCoursesCache;
		}

		private bool IsTempCourseUpdatedRecent(string courseId)
		{
			if (tempCourseUpdateTime.TryGetValue(courseId, out var lastFetchTime))
				return lastFetchTime > DateTime.Now.Subtract(tempCourseUpdateEvery);
			return false;
		}
	}
}