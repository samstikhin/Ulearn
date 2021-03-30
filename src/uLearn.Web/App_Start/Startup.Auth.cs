using System;
using System.IO;
using System.Web.Configuration;
using Database.DataContexts;
using Database.Extensions;
using Database.Models;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.Owin;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Owin;
using Microsoft.Owin.Host.SystemWeb;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.Interop;
using Owin;
using uLearn.Web.Kontur.Passport;
using uLearn.Web.LTI;
using uLearn.Web.Microsoft.Owin.Security.VK;
using Ulearn.Core;
using Ulearn.Core.Configuration;
using uLearn.Web.SameSite;
using Web.Api.Configuration;

namespace uLearn.Web
{
	public partial class Startup
	{
		// For more information on configuring authentication, please visit http://go.microsoft.com/fwlink/?LinkId=301864
		public void ConfigureAuth(IAppBuilder app)
		{
			app.CreatePerOwinContext(() => new ULearnUserManager(new ULearnDb()));

			var configuration = ApplicationConfiguration.Read<WebApiConfiguration>();

			// Enable the application to use a cookie to store information for the signed in user
			var cookieKeyRingDirectory = new DirectoryInfo(Path.Combine(Utils.GetAppPath(), configuration.Web.CookieKeyRingDirectory));
			app.UseCookieAuthentication(new CookieAuthenticationOptions
			{
				AuthenticationType = "Identity.Application",
				CookieName = configuration.Web.CookieName,
				CookieDomain = configuration.Web.CookieDomain,
				CookieSameSite = configuration.Web.CookieSecure ? SameSiteMode.None : SameSiteMode.Lax, // https://docs.microsoft.com/ru-ru/aspnet/samesite/system-web-samesite
				CookieManager = configuration.Web.CookieSecure ? new SameSiteCookieManager(new SystemWebCookieManager()) : (ICookieManager)new SystemWebCookieManager(), // https://docs.microsoft.com/ru-ru/aspnet/samesite/csmvc
				CookieSecure = configuration.Web.CookieSecure ? CookieSecureOption.Always : CookieSecureOption.Never,

				LoginPath = new PathString("/Login"),
				Provider = new CookieAuthenticationProvider
				{
					OnValidateIdentity = SecurityStampValidator.OnValidateIdentity<ULearnUserManager, ApplicationUser, string>(
						validateInterval: TimeSpan.FromMinutes(30),
						regenerateIdentityCallback: (manager, user) => manager.CreateIdentityAsync(user, "Identity.Application"),
						getUserIdCallback: identity => identity.GetUserId()
					)
				},
				/* Configure sharing cookies between application.
			       See https://docs.microsoft.com/en-us/aspnet/core/security/cookie-sharing?tabs=aspnetcore2x for details */
				TicketDataFormat = new AspNetTicketDataFormat(
					new DataProtectorShim(
						DataProtectionProvider.Create(cookieKeyRingDirectory, builder => builder.SetApplicationName("ulearn"))
							.CreateProtector(
								"Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware",
								"Identity.Application",
								// DefaultAuthenticationTypes.ApplicationCookie,
								"v2"))),
			});

			// Use a cookie to temporarily store information about a user logging in with a third party login provider
			app.UseExternalSignInCookie(DefaultAuthenticationTypes.ExternalCookie);

			// Uncomment the following lines to enable logging in with third party login providers
			//app.UseMicrosoftAccountAuthentication(
			//	clientId: "",
			//	clientSecret: "");

			//app.UseTwitterAuthentication(
			//	consumerKey: "hC6XpJy0OPVkbvGzRIOJRA",
			//	  consumerSecret: "cEDewTtU7RKHimj2D1IpD75HUKnjVeobdSNhjAAQ");

			var vkAppId = WebConfigurationManager.AppSettings["owin.vk.appId"];
			var vkAppSecret = WebConfigurationManager.AppSettings["owin.vk.appSecret"];
			app.UseVkAuthentication(vkAppId, vkAppSecret);
			//app.UseFacebookAuthentication(
			//   appId: "",
			//   appSecret: "");

			var konturPassportClientId = WebConfigurationManager.AppSettings["owin.konturPassport.clientId"];
			if (!string.IsNullOrEmpty(konturPassportClientId))
				app.UseKonturPassportAuthentication(konturPassportClientId);

			//app.UseGoogleAuthentication();
			app.UseLtiAuthentication();
		}
	}
}