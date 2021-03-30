using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Database.Models;
using Microsoft.AspNetCore.Identity;

namespace Database.Repos
{
	public static class AppUserQueryExtensions
	{
		public static IQueryable<ApplicationUser> FilterByRole(this IQueryable<ApplicationUser> applicationUsers, IdentityRole role)
		{
			return role == null
				? applicationUsers
				: applicationUsers.Where(u => u.Roles.Any(r => r.RoleId == role.Id));
		}

		public static IQueryable<ApplicationUser> FilterByUserIds(this IQueryable<ApplicationUser> applicationUsers, params List<string>[] idLists)
		{
			HashSet<string> goodIds = null;
			foreach (var idList in idLists.Where(idList => idList != null))
			{
				if (goodIds == null)
					goodIds = new HashSet<string>(idList);
				else
					goodIds.IntersectWith(idList);
			}

			return goodIds == null
				? applicationUsers
				: applicationUsers.Where(user => goodIds.Contains(user.Id));
		}

		/* Pass count=0 to disable limiting */
		public static async Task<List<UserRolesInfo>> GetUserRolesInfoAsync(this IQueryable<ApplicationUser> applicationUsers, int count, UserManager<ApplicationUser> userManager)
		{
			IQueryable<ApplicationUser> users = applicationUsers.OrderBy(u => u.UserName);
			if (count > 0)
				users = users.Take(count);

			var userRolesInfos = new List<UserRolesInfo>();
			foreach (var user in users.ToList())
			{
				userRolesInfos.Append(new UserRolesInfo
				{
					UserId = user.Id,
					UserName = user.UserName,
					UserVisibleName = user.VisibleName,
					Roles = (await userManager.GetRolesAsync(user).ConfigureAwait(false)).ToList()
				});
			}

			return userRolesInfos;
		}
	}
}