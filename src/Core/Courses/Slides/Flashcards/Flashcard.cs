using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using Ulearn.Common;
using Ulearn.Core.Courses.Slides.Blocks;

namespace Ulearn.Core.Courses.Slides.Flashcards
{
	[XmlType("Flashcard", Namespace = "https://ulearn.me/schema/v2")]
	public class Flashcard : IFlashcard
	{
		[XmlAttribute("id")]
		public string Id { get; set; }

		[XmlElement(ElementName = "theorySlideId")]
		public Guid[] TheorySlidesIds { get; set; }

		[XmlElement(ElementName = "question")]
		public FlashcardContent Question { get; set; }

		[XmlElement(ElementName = "answer")]
		public FlashcardContent Answer { get; set; }

		public void BuildUp(SlideLoadingContext context, Slide flashcardSlide)
		{
			if (Answer is null)
			{
				Answer = new FlashcardContent();
			}

			if (Question is null)
			{
				Question = new FlashcardContent();
			}

			if (Answer.Blocks is null)
			{
				Answer.Blocks = new SlideBlock[0];
			}

			if (Question.Blocks is null)
			{
				Question.Blocks = new SlideBlock[0];
			}

			if (TheorySlidesIds is null)
			{
				TheorySlidesIds = new Guid[0];
			}

			var slideLoadingContext = new SlideBuildingContext(context, flashcardSlide);
			Answer.Blocks = Answer.Blocks
				.SelectMany(x => x.BuildUp(slideLoadingContext, ImmutableHashSet<string>.Empty))
				.ToArray();
			Question.Blocks = Question.Blocks
				.SelectMany(x => x.BuildUp(slideLoadingContext, ImmutableHashSet<string>.Empty))
				.ToArray();
		}

		public void Validate(SlideLoadingContext context, Slide flashcardSlide)
		{
			var slideBuildingContext = new SlideBuildingContext(context, flashcardSlide);
			foreach (var block in Question.Blocks)
			{
				block.Validate(slideBuildingContext);
			}

			foreach (var block in Answer.Blocks)
			{
				block.Validate(slideBuildingContext);
			}
		}

		public string RenderQuestion()
		{
			var content = new StringBuilder();
			foreach (var block in Question.Blocks)
				content.Append(RenderBlock(block));
			return content.ToString();
		}

		public string RenderAnswer()
		{
			var content = new StringBuilder();
			foreach (var block in Answer.Blocks)
				content.Append(RenderBlock(block));
			return content.ToString();
		}

		public static string RenderBlock(SlideBlock block)
		{
			switch (block)
			{
				case MarkdownBlock markdownBlock:
					return markdownBlock.TryGetText().RenderMarkdown();
				case CodeBlock codeBlock:
					return $"\n<textarea class=\"code code-sample\" data-lang=\"{codeBlock.Language.GetName()}\">{codeBlock.Code}</textarea>";
				case TexBlock texBlock:
					var lines = texBlock.TexLines.Select(x => $"<div class=\"tex\">{x.Trim()}</div>");
					return string.Join("\n", lines);
				default:
					return block.TryGetText();
			}
		}
	}
}