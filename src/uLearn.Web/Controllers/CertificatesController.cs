using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Mvc;
using Database;
using Database.DataContexts;
using Database.Extensions;
using Database.Models;
using Microsoft.AspNet.Identity;
using uLearn.Web.Extensions;
using Ulearn.Core;
using Ulearn.Core.Courses;

namespace uLearn.Web.Controllers
{
	public class CertificatesController : Controller
	{
		private readonly CertificatesRepo certificatesRepo;
		private readonly UserRolesRepo userRolesRepo;
		private readonly ULearnUserManager userManager;
		private readonly CourseManager courseManager;
		private readonly CertificateGenerator certificateGenerator;

		public CertificatesController()
			: this(new ULearnDb(), WebCourseManager.Instance)
		{
		}

		public CertificatesController(ULearnDb db, WebCourseManager courseManager)
		{
			this.courseManager = courseManager;

			certificatesRepo = new CertificatesRepo(db);
			userRolesRepo = new UserRolesRepo(db);
			userManager = new ULearnUserManager(db);
			certificateGenerator = new CertificateGenerator(db, courseManager);
		}

		[AllowAnonymous]
		public async Task<ActionResult> Index(string userId = "")
		{
			if (string.IsNullOrEmpty(userId) && User.Identity.IsAuthenticated)
				userId = User.Identity.GetUserId();

			if (string.IsNullOrEmpty(userId))
				return HttpNotFound();

			var user = await userManager.FindByIdAsync(userId);
			if (user == null)
				return HttpNotFound();

			var certificates = certificatesRepo.GetUserCertificates(userId);
			var coursesTitles = courseManager.GetCourses().ToDictionary(c => c.Id.ToLower(), c => c.Title);

			return View("List", new UserCertificatesViewModel
			{
				User = user,
				Certificates = certificates,
				CoursesTitles = coursesTitles,
			});
		}

		[Authorize]
		public ActionResult Partial()
		{
			var userId = User.Identity.GetUserId();
			var user = userManager.FindById(userId);
			if (user == null)
				return HttpNotFound();

			var certificates = certificatesRepo.GetUserCertificates(userId);
			var coursesTitles = courseManager.GetCourses().ToDictionary(c => c.Id.ToLower(), c => c.Title);

			return PartialView("ListPartial", new UserCertificatesViewModel
			{
				User = user,
				Certificates = certificates,
				CoursesTitles = coursesTitles,
			});
		}

		public ActionResult CertificateById(Guid certificateId)
		{
			var redirect = this.GetRedirectToUrlWithTrailingSlash();
			if (redirect != null)
				return RedirectPermanent(redirect);

			var certificate = certificatesRepo.FindCertificateById(certificateId);
			if (certificate == null)
				return HttpNotFound();

			if (certificate.IsPreview && !userRolesRepo.HasUserAccessToCourse(User.Identity.GetUserId(), certificate.Template.CourseId, CourseRole.Instructor))
				return HttpNotFound();

			var course = courseManager.GetCourse(certificate.Template.CourseId);

			certificateGenerator.EnsureCertificateTemplateIsUnpacked(certificate.Template);

			var certificateUrl = Url.RouteUrl("Certificate", new { certificateId = certificate.Id }, Request.GetRealScheme());
			var renderedCertificate = certificateGenerator.RenderCertificate(certificate, course, certificateUrl);

			return View("Certificate", new CertificateViewModel
			{
				Course = course,
				Certificate = certificate,
				RenderedCertificate = renderedCertificate,
			});
		}

		public ActionResult CertificateFile(Guid certificateId, string path)
		{
			var certificate = certificatesRepo.FindCertificateById(certificateId);
			if (certificate == null)
				return HttpNotFound();

			if (path.Contains(".."))
				return HttpNotFound();

			return RedirectPermanent($"/Certificates/{certificate.Template.ArchiveName}/{path}");
		}
	}

	public class UserCertificatesViewModel
	{
		public ApplicationUser User { get; set; }
		public List<Certificate> Certificates { get; set; }
		public Dictionary<string, string> CoursesTitles { get; set; }
	}

	public class CertificateViewModel
	{
		public Course Course { get; set; }
		public Certificate Certificate { get; set; }
		public string RenderedCertificate { get; set; }
	}
}