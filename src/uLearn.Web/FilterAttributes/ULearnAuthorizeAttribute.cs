using System;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using Database.DataContexts;
using Database.Extensions;
using Database.Models;
using Microsoft.AspNet.Identity;

namespace uLearn.Web.FilterAttributes
{
	public class ULearnAuthorizeAttribute : AuthorizeAttribute
	{
		public CourseRole MinAccessLevel = CourseRole.Student;

		protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
		{
			var request = filterContext.HttpContext.Request;
			if (request.HttpMethod == "POST" && request.UrlReferrer != null)
			{
				filterContext.Result = new RedirectToRouteResult(new RouteValueDictionary(new { action = "Index", controller = "Login", returnUrl = request.UrlReferrer.PathAndQuery }));
				return;
			}

			base.HandleUnauthorizedRequest(filterContext);
		}

		protected override bool AuthorizeCore(HttpContextBase httpContext)
		{
			if (httpContext == null)
				throw new ArgumentNullException(nameof(httpContext));

			var user = httpContext.User;
			if (!user.Identity.IsAuthenticated)
				return false;

			var userId = httpContext.User.Identity.GetUserId();
			var db = new ULearnDb();
			var usersRepo = new UsersRepo(db);
			if (usersRepo.FindUserById(userId) == null) // I.e. if user has been deleted
				return false;

			if (MinAccessLevel == CourseRole.Student && !ShouldBeSysAdmin)
				return true;

			var userRolesRepo = new UserRolesRepo(db);
			if (ShouldBeSysAdmin && userRolesRepo.IsSystemAdministrator(userId))
				return true;

			if (MinAccessLevel == CourseRole.Student)
				return false;

			var courseIds = httpContext.Request.Unvalidated.QueryString.GetValues("courseId");
			if (courseIds == null)
				return user.HasAccess(MinAccessLevel);

			if (courseIds.Length != 1)
				return false;

			return userRolesRepo.HasUserAccessToCourse(userId, courseIds[0], MinAccessLevel);
		}

		public bool ShouldBeSysAdmin { get; set; }

		public new string Users
		{
			set { throw new NotSupportedException(); }
		}

		private static readonly char[] delims = { ',' };

		private static string[] SplitString(string original)
		{
			if (String.IsNullOrEmpty(original))
			{
				return new string[0];
			}

			return original
				.Split(delims)
				.Select(piece => piece.Trim())
				.Where(s => !String.IsNullOrEmpty(s))
				.ToArray();
		}
	}
}