using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using Database.Extensions;
using Database.Models;
using JetBrains.Annotations;
using Ulearn.Common;
using Ulearn.Common.Extensions;

namespace Database.DataContexts
{
	public class CoursesRepo
	{
		private readonly ULearnDb db;

		public CoursesRepo()
			: this(new ULearnDb())
		{
		}

		public CoursesRepo(ULearnDb db)
		{
			this.db = db;
		}

		public CourseVersion GetPublishedCourseVersion(string courseId)
		{
			return db.CourseVersions.Where(v => v.CourseId == courseId && v.PublishTime != null).OrderByDescending(v => v.PublishTime).FirstOrDefault();
		}

		public IEnumerable<CourseVersion> GetCourseVersions(string courseId)
		{
			return db.CourseVersions.Where(v => v.CourseId == courseId).OrderByDescending(v => v.LoadingTime);
		}

		public async Task<CourseVersion> AddCourseVersion(string courseId, Guid versionId, string authorId,
			string pathToCourseXml, string repoUrl, string commitHash, string description)
		{
			var courseVersion = new CourseVersion
			{
				Id = versionId,
				CourseId = courseId,
				LoadingTime = DateTime.Now,
				PublishTime = null,
				AuthorId = authorId,
				PathToCourseXml = pathToCourseXml,
				CommitHash = commitHash,
				Description = description,
				RepoUrl = repoUrl
			};
			db.CourseVersions.Add(courseVersion);
			await db.SaveChangesAsync();

			return courseVersion;
		}

		public async Task MarkCourseVersionAsPublished(Guid versionId)
		{
			var courseVersion = db.CourseVersions.Find(versionId);
			if (courseVersion == null)
				return;

			courseVersion.PublishTime = DateTime.Now;
			await db.SaveChangesAsync();
		}

		public async Task DeleteCourseVersion(string courseId, Guid versionId)
		{
			var courseVersion = db.CourseVersions.Find(versionId);
			if (courseVersion == null)
				return;

			if (!string.Equals(courseVersion.CourseId, courseId, StringComparison.OrdinalIgnoreCase))
				return;

			db.CourseVersions.Remove(courseVersion);
			await db.SaveChangesAsync();
		}

		/* Course accesses */

		private IEnumerable<CourseAccess> GetActualEnabledCourseAccesses(string courseId = null, string userId = null)
		{
			var queryable = db.CourseAccesses.Include(a => a.User);
			if (courseId != null)
				queryable = queryable.Where(x => x.CourseId == courseId);
			if (userId != null)
				queryable = queryable.Where(x => x.UserId == userId);
			return queryable.ToList()
				.GroupBy(x => x.CourseId + x.UserId + x.AccessType, StringComparer.OrdinalIgnoreCase)
				.Select(gr => gr.Last())
				.Where(ca => ca.IsEnabled);
		}

		public async Task<CourseAccess> GrantAccess(string courseId, string userId, CourseAccessType accessType, string grantedById, string comment)
		{
			courseId = courseId.ToLower();
			var currentAccess = new CourseAccess
			{
				CourseId = courseId,
				UserId = userId,
				AccessType = accessType,
				GrantedById = grantedById,
				GrantTime = DateTime.Now.ToUniversalTime(),
				IsEnabled = true,
				Comment = comment
			};
			db.CourseAccesses.Add(currentAccess);

			await db.SaveChangesAsync().ConfigureAwait(false);
			return db.CourseAccesses.Include(a => a.GrantedBy).Single(a => a.Id == currentAccess.Id);
		}

		public async Task<List<CourseAccess>> RevokeAccess(string courseId, string userId, CourseAccessType accessType, string grantedById, string comment)
		{
			courseId = courseId.ToLower();
			var revoke = new CourseAccess
			{
				UserId = userId,
				GrantTime = DateTime.Now.ToUniversalTime(),
				GrantedById = grantedById,
				Comment = comment,
				IsEnabled = false,
				CourseId = courseId,
				AccessType = accessType
			};
			db.CourseAccesses.Add(revoke);

			await db.SaveChangesAsync().ConfigureAwait(false);
			return new List<CourseAccess> { revoke };
		}

		public List<CourseAccess> GetCourseAccesses(string courseId)
		{
			return GetActualEnabledCourseAccesses(courseId: courseId).ToList();
		}

		public List<CourseAccess> GetCourseAccesses(string courseId, string userId)
		{
			return GetActualEnabledCourseAccesses(courseId: courseId, userId: userId).ToList();
		}

		public DefaultDictionary<string, List<CourseAccess>> GetCoursesAccesses(IEnumerable<string> coursesIds)
		{
			return GetActualEnabledCourseAccesses()
				.Where(a => coursesIds.Contains(a.CourseId, StringComparer.OrdinalIgnoreCase))
				.GroupBy(a => a.CourseId)
				.ToDictionary(g => g.Key, g => g.ToList())
				.ToDefaultDictionary();
		}

		public bool HasCourseAccess(string userId, string courseId, CourseAccessType accessType)
		{
			return GetActualEnabledCourseAccesses(courseId: courseId, userId: userId).Any(a => a.AccessType == accessType);
		}

		public async Task<List<CourseAccess>> GetUserAccessHistory(string userId)
		{
			return db.CourseAccesses.Where(x => x.UserId == userId).ToList();
		}

		public async Task<List<CourseAccess>> GetUserAccessHistoryByCourseId(string userId, string courseId)
		{
			courseId = courseId.ToLower();
			return db.CourseAccesses.Where(x => x.UserId == userId && x.CourseId == courseId).ToList();
		}

		// Add new and remove old course file
		public async Task AddCourseFile(string courseId, Guid versionId, byte[] content)
		{
			var file = new CourseFile
			{
				CourseId = courseId,
				CourseVersionId = versionId,
				File = content
			};
			db.CourseFiles.RemoveRange(db.CourseFiles.Where(f => f.CourseId.Equals(courseId, StringComparison.OrdinalIgnoreCase)));
			db.CourseFiles.Add(file);
			await db.SaveChangesAsync();
		}

		[CanBeNull]
		public CourseFile GetCourseFile(string courseId)
		{
			return db.CourseFiles.FirstOrDefault(f => f.CourseId.Equals(courseId, StringComparison.OrdinalIgnoreCase));
		}

		public List<CourseFile> GetCourseFiles(IEnumerable<string> existingOnDiskCourseIds)
		{
			return db.CourseFiles.Where(a => !existingOnDiskCourseIds.Contains(a.CourseId)).ToList();
		}

		[CanBeNull]
		public CourseGit GetCourseRepoSettings(string courseId)
		{
			var data = db.CourseGitRepos.Where(v => v.CourseId == courseId).OrderByDescending(v => v.CreateTime).FirstOrDefault();
			if (data?.RepoUrl == null)
				return null;
			return data;
		}

		public async Task SetCourseRepoSettings(CourseGit courseGit)
		{
			courseGit.CreateTime = DateTime.Now;
			db.CourseGitRepos.Add(courseGit);
			await db.SaveChangesAsync();
		}

		public async Task RemoveCourseRepoSettings(string courseId)
		{
			var courseGit = new CourseGit { CourseId = courseId };
			await SetCourseRepoSettings(courseGit).ConfigureAwait(false);
		}

		public List<CourseGit> FindCoursesByRepoUrl(string repoUrl)
		{
			return db.CourseGitRepos.GroupBy(r => r.CourseId).Select(g => g.OrderByDescending(r => r.CreateTime).FirstOrDefault()).Where(r => r.RepoUrl == repoUrl).ToList();
		}

		public async Task UpdateKeysByRepoUrl(string repoUrl, string publicKey, string privateKey)
		{
			using (var transaction = db.Database.BeginTransaction())
			{
				var repos = FindCoursesByRepoUrl(repoUrl);
				foreach (var repo in repos)
				{
					repo.PublicKey = publicKey;
					repo.PrivateKey = privateKey;
					await SetCourseRepoSettings(repo).ConfigureAwait(false);
				}

				transaction.Commit();
			}
		}
	}
}