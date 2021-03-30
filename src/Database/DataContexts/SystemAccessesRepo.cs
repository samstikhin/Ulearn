using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Database.Models;

namespace Database.DataContexts
{
	public class SystemAccessesRepo
	{
		private readonly ULearnDb db;

		public SystemAccessesRepo(ULearnDb db)
		{
			this.db = db;
		}

		public SystemAccessesRepo()
			: this(new ULearnDb())
		{
		}

		public async Task<SystemAccess> GrantAccess(string userId, SystemAccessType accessType, string grantedById)
		{
			var currentAccess = db.SystemAccesses.FirstOrDefault(a => a.UserId == userId && a.AccessType == accessType);
			if (currentAccess == null)
			{
				currentAccess = new SystemAccess
				{
					UserId = userId,
					AccessType = accessType,
				};
				db.SystemAccesses.Add(currentAccess);
			}

			currentAccess.GrantedById = grantedById;
			currentAccess.GrantTime = DateTime.Now;
			currentAccess.IsEnabled = true;

			await db.SaveChangesAsync();
			return db.SystemAccesses.Include(a => a.GrantedBy).Single(a => a.Id == currentAccess.Id);
		}

		public async Task<List<SystemAccess>> RevokeAccess(string userId, SystemAccessType accessType)
		{
			var accesses = db.SystemAccesses.Where(a => a.UserId == userId && a.AccessType == accessType).ToList();
			foreach (var access in accesses)
				access.IsEnabled = false;

			await db.SaveChangesAsync();
			return accesses;
		}

		public List<SystemAccess> GetSystemAccesses()
		{
			return db.SystemAccesses.Include(a => a.User).Where(a => a.IsEnabled).ToList();
		}

		public List<SystemAccess> GetSystemAccesses(string userId)
		{
			return db.SystemAccesses.Include(a => a.User).Where(a => a.UserId == userId && a.IsEnabled).ToList();
		}

		public bool HasSystemAccess(string userId, SystemAccessType accessType)
		{
			return db.SystemAccesses.Any(a => a.UserId == userId && a.AccessType == accessType && a.IsEnabled);
		}
	}
}