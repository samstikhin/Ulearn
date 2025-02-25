﻿using System;
using AntiPlagiarism.Api.Defaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AntiPlagiarism.Api.Models.Parameters
{
	public class GetAuthorPlagiarismsParameters : AntiPlagiarismApiParameters
	{
		[BindRequired]
		[FromQuery(Name = "author_id")]
		public Guid AuthorId { get; set; }

		[BindRequired]
		[FromQuery(Name = "task_id")]
		public Guid TaskId { get; set; }

		[FromQuery(Name = "last_submissions_count")]
		public int LastSubmissionsCount { get; set; } = GetAuthorPlagiarismsDefaults.LastSubmissionsCount;
	}
}