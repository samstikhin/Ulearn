using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;
using Database.Models;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Ulearn.Common;
using Ulearn.Core;

namespace Database.Repos.Users
{
	/* TODO (andgein): This repo is not fully migrated to .NET Core and EF Core */
	public class UsersRepo : IUsersRepo
	{
		private readonly UlearnDb db;
		private readonly UlearnUserManager userManager;
		private IdentityRole sysAdminRole = null;

		public const string UlearnBotUsername = "ulearn-bot";

		public UsersRepo(UlearnDb db, UlearnUserManager userManager)
		{
			this.db = db;
			this.userManager = userManager;
		}

		public async Task<ApplicationUser> FindUserByIdAsync(string userId)
		{
			var user = await db.Users.FindAsync(userId).ConfigureAwait(false);
			if (user == null)
				return null;

			return user.IsDeleted ? null : user;
		}

		public Task<List<string>> FindUsersBySocialProviderKeyAsync(string providerKey)
		{
			return db.UserLogins.Where(login => login.ProviderKey == providerKey).Select(login => login.UserId).Distinct().ToListAsync();
		}

		public async Task<List<string>> GetUserIdsWithLmsRoleAsync(LmsRoleType lmsRole)
		{
			var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == lmsRole.ToString()).ConfigureAwait(false);
			if (role == null)
				return new List<string>();
			return db.Users.Where(u => !u.IsDeleted).FilterByRole(role).Select(u => u.Id).ToList();
		}

		public Task<List<string>> GetSysAdminsIdsAsync()
		{
			return GetUserIdsWithLmsRoleAsync(LmsRoleType.SysAdmin);
		}

		public async Task ChangeTelegram(string userId, long chatId, string chatTitle)
		{
			var user = await FindUserByIdAsync(userId).ConfigureAwait(false);
			if (user == null)
				return;

			user.TelegramChatId = chatId;
			user.TelegramChatTitle = chatTitle;
			await db.SaveChangesAsync().ConfigureAwait(false);
		}

		public async Task ConfirmEmail(string userId, bool isConfirmed = true)
		{
			var user = await FindUserByIdAsync(userId).ConfigureAwait(false);
			if (user == null)
				return;

			user.EmailConfirmed = isConfirmed;
			await db.SaveChangesAsync().ConfigureAwait(false);
		}

		public Task UpdateLastConfirmationEmailTime(ApplicationUser user)
		{
			user.LastConfirmationEmailTime = DateTime.Now;
			return db.SaveChangesAsync();
		}

		public Task ChangeEmail(ApplicationUser user, string email)
		{
			user.Email = email;
			user.EmailConfirmed = false;
			return db.SaveChangesAsync();
		}

		[NotNull]
		public async Task<ApplicationUser> GetUlearnBotUser()
		{
			var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == UlearnBotUsername);
			if (user == null)
				throw new NotFoundException($"Ulearn bot user (username = {UlearnBotUsername}) not found");
			return user;
		}

		public async Task<string> GetUlearnBotUserId()
		{
			var user = await GetUlearnBotUser();
			return user.Id;
		}

		public async Task CreateUlearnBotUserIfNotExistsAsync()
		{
			var ulearnBotFound = await db.Users.AnyAsync(u => u.UserName == UlearnBotUsername).ConfigureAwait(false);
			if (!ulearnBotFound)
			{
				var user = new ApplicationUser
				{
					UserName = UlearnBotUsername,
					FirstName = "Ulearn",
					LastName = "bot",
					Email = "support@ulearn.me",
				};
				await userManager.CreateAsync(user, StringUtils.GenerateSecureAlphanumericString(10)).ConfigureAwait(false);

				await db.SaveChangesAsync().ConfigureAwait(false);
			}
		}

		public List<ApplicationUser> FindUsersByUsernameOrEmail(string usernameOrEmail)
		{
			return db.Users.Where(u => (u.UserName == usernameOrEmail || u.Email == usernameOrEmail) && !u.IsDeleted).ToList();
		}

		public Task<List<ApplicationUser>> GetUsersByIdsAsync(IEnumerable<string> usersIds)
		{
			return db.Users.Where(u => usersIds.Contains(u.Id) && !u.IsDeleted).ToListAsync();
		}

		public Task DeleteUserAsync(ApplicationUser user)
		{
			user.IsDeleted = true;
			/* Change name to make creating new user with same name possible */
			user.UserName = user.UserName + "__deleted__" + (new Random().Next(100000));
			return db.SaveChangesAsync();
		}

		public bool IsSystemAdministrator(ApplicationUser user)
		{
			if (user?.Roles == null)
				return false;

			if (sysAdminRole == null)
				sysAdminRole = db.Roles.First(r => r.Name == LmsRoleType.SysAdmin.ToString());

			return user.Roles.Any(role => role.RoleId == sysAdminRole.Id);
		}

		public async Task<bool> IsSystemAdministrator(string userId)
		{
			return (await GetSysAdminsIdsAsync()).Contains(userId);
		}
	}

	/* System.String is not available for table-valued functions so we need to create ComplexTyped wrapper */

	[ComplexType]
	public class UserIdWrapper
	{
		public UserIdWrapper(string userId)
		{
			Id = userId;
		}

		public string Id { get; set; }
	}
}