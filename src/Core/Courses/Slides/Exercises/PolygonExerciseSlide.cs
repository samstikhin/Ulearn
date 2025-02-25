﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using Ulearn.Common;
using Ulearn.Core.Courses.Slides.Blocks;
using Ulearn.Core.Courses.Slides.Exercises.Blocks;

namespace Ulearn.Core.Courses.Slides.Exercises
{
	[XmlRoot("slide.polygon", IsNullable = false, Namespace = "https://ulearn.me/schema/v2")]
	public class PolygonExerciseSlide : ExerciseSlide
	{
		[XmlElement("polygonPath")]
		public string PolygonPath { get; set; }

		
		public override void Validate(SlideLoadingContext context)
		{
			if (string.IsNullOrEmpty(PolygonPath))
				throw new CourseLoadingException("В slide.polygon должен находиться атрибут polygonPath");
			base.Validate(context);
		}

		public override void BuildUp(SlideLoadingContext context)
		{
			var statementsPath = Path.Combine(context.Unit.Directory.FullName, PolygonPath, "statements");
			Blocks = GetBlocksProblem(statementsPath, context.CourseId, Id)
				.Concat(Blocks.Where(block => !(block is MarkdownBlock)))
				.ToArray();

			var polygonExercise = Blocks.Single(block => block is PolygonExerciseBlock) as PolygonExerciseBlock;
			polygonExercise!.ExerciseDirPath = Path.Combine(PolygonPath);
			var problem = GetProblem(Path.Combine(context.Unit.Directory.FullName, PolygonPath, "problem.xml"), context.CourseSettings.DefaultLanguage);
			polygonExercise.TimeLimitPerTest = problem.TimeLimit;
			polygonExercise.TimeLimit = (int)Math.Ceiling(problem.TimeLimit * problem.TestCount);
			polygonExercise.UserCodeFilePath = problem.PathAuthorSolution;
			polygonExercise.Language = LanguageHelpers.GuessByExtension(new FileInfo(polygonExercise.UserCodeFilePath));
			polygonExercise.DefaultLanguage = context.CourseSettings.DefaultLanguage;
			polygonExercise.RunCommand = $"python3.8 main.py {polygonExercise.Language} {polygonExercise.TimeLimitPerTest} {polygonExercise.UserCodeFilePath.Split('/', '\\')[1]}";
			Title = problem.Title;
			PrepareSolution(Path.Combine(context.Unit.Directory.FullName, PolygonPath, polygonExercise.UserCodeFilePath));

			base.BuildUp(context);
		}

		private IEnumerable<SlideBlock> GetBlocksProblem(string statementsPath, string courseId, Guid slideId)
		{
			var markdownBlock = Blocks.FirstOrDefault(block => block is MarkdownBlock);
			if (markdownBlock != null)
			{
				yield return markdownBlock;
			}
			else if (Directory.Exists(Path.Combine(statementsPath, ".html")))
			{
				var htmlDirectoryPath = Path.Combine(statementsPath, ".html", "russian");
				var htmlData = File.ReadAllText(Path.Combine(htmlDirectoryPath, "problem.html"));
				yield return RenderFromHtml(htmlData);
			}

			var pdfLink = PolygonPath + "/statements/.pdf/russian/problem.pdf";
			yield return new MarkdownBlock($"[Скачать условия задачи в формате PDF]({pdfLink})");
		}
		private static SlideBlock RenderFromHtml(string html)
		{
			var match = new Regex("(<DIV class=[\"']problem-statement['\"]>.*)</BODY>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
				.Match(html);
			if (!match.Success)
				throw new Exception();
			var body = match.Groups[1].Value;
			var processedBody = body
				.Replace("$$$$$$", "$$")
				.Replace("$$$", "$");
			return new HtmlBlock($"<div class=\"math-tex problem\">{processedBody}</div>");
		}

		private static MarkdownBlock GetLinkOnPdf(string courseId, string slideId)
		{
			var link = $"/Exercise/GetPdf?courseId={courseId}&slideId={slideId}";
			return new MarkdownBlock($"[Скачать условия задачи в формате PDF]({link})");
		}

		private static Problem GetProblem(string pathToXml, Language? defaultLanguage)
		{
			var document = new XmlDocument();
			document.Load(pathToXml);
			
			var nameNodeList = document.SelectNodes(@"/problem/names/name");
			var name = GetNodes(nameNodeList)
				.First(node => node.Attributes!["language"].Value == "russian")
				.Attributes!["value"]!.Value ?? "";
			
			var timeLimit = document.SelectSingleNode(@"/problem/judging/testset/time-limit")!.InnerText;
			var testCount = document.SelectSingleNode(@"/problem/judging/testset/test-count")!.InnerText;
			var solutionPath = GetSolutionPath(document, defaultLanguage);
			return new Problem
			{
				TimeLimit = int.Parse(timeLimit) / 1000d,
				TestCount = int.Parse(testCount),
				Title = name,
				PathAuthorSolution = solutionPath
			};
		}

		private static string GetSolutionPath(XmlNode xmlProblem, Language? defaultLanguage)
		{
			var solutionNodeList = xmlProblem.SelectNodes(@"/problem/assets/solutions/solution");
			var solutions = GetNodes(solutionNodeList)
				.Select(node => new
				{
					Tag = node.Attributes!["tag"].Value,
					Path = node.ChildNodes.Item(0)!.Attributes!["path"].Value
				})
				.ToArray();
			var mainSolution = solutions.First(s => s.Tag == "main");
			var acceptedSolution = solutions.Where(s => s.Tag == "accepted").ToArray();

			var solutionWithLanguageAsDefaultInCourse = new[] { mainSolution }
				.Concat(acceptedSolution)
				.FirstOrDefault(s => LanguageHelpers.GuessByExtension(new FileInfo(s.Path)) == defaultLanguage);
			
			return solutionWithLanguageAsDefaultInCourse?.Path ?? mainSolution.Path;
		}

		private static IEnumerable<XmlNode> GetNodes(XmlNodeList nodeList)
			=> Enumerable.Range(0, nodeList!.Count).Select(nodeList.Item);
		
		private void PrepareSolution(string solutionFilename)
		{
			var solution = File.ReadAllText(solutionFilename);
			if (solution.Contains("//region Task") && solution.Contains("//endregion Task"))
				return;
			var solutionWithRegion = $"//region Task\n\n{solution}\n\n//endregion Task";
			File.WriteAllText(solutionFilename, solutionWithRegion);
		}
	}

	internal class Problem
	{
		public double TimeLimit { get; set; }
		public int TestCount { get; set; }
		public string Title { get; set; }
		public string PathAuthorSolution { get; set; }
	}
	
}