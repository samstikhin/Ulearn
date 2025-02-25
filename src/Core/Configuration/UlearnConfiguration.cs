﻿using System;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Ulearn.Core.Configuration
{
	public static class ApplicationConfiguration
	{
		private static readonly Lazy<IConfigurationRoot> configuration = new Lazy<IConfigurationRoot>(GetConfiguration);

		public static T Read<T>() where T : UlearnConfigurationBase
		{
			var r = configuration.Value.Get<T>();
			return r;
		}

		public static IConfigurationRoot GetConfiguration()
		{
			var applicationPath = string.IsNullOrEmpty(Utils.WebApplicationPhysicalPath)
				? AppDomain.CurrentDomain.BaseDirectory
				: Utils.WebApplicationPhysicalPath;
			var configurationBuilder = new ConfigurationBuilder()
				.SetBasePath(applicationPath);
			configurationBuilder.AddEnvironmentVariables();
			BuildAppSettingsConfiguration(configurationBuilder);
			return configurationBuilder.Build();
		}

		public static void BuildAppSettingsConfiguration(IConfigurationBuilder configurationBuilder)
		{
			configurationBuilder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
			var environmentName = Environment.GetEnvironmentVariable("UlearnEnvironmentName");
			if (environmentName != null && environmentName.ToLower().Contains("local"))
				configurationBuilder.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
		}

		private static void DisposeConfiguration(IConfigurationRoot configuration) // https://github.com/aspnet/Extensions/issues/786
		{
			foreach (var provider in configuration.Providers.OfType<JsonConfigurationProvider>())
				if (provider.Source.FileProvider is PhysicalFileProvider pfp)
					pfp.Dispose();
		}
	}

	public class UlearnConfigurationBase
	{
		// Достаточно поверхностного копирования. Потому что важно только сохранить ссылку на объект конфигурации целиком
		public void SetFrom(UlearnConfigurationBase other)
		{
			var thisProperties = GetType().GetProperties();
			var otherProperties = other.GetType().GetProperties();

			foreach (var otherProperty in otherProperties)
			{
				foreach (var thisProperty in thisProperties)
				{
					if (otherProperty.Name == thisProperty.Name && otherProperty.PropertyType == thisProperty.PropertyType)
					{
						thisProperty.SetValue(this, otherProperty.GetValue(other));
						break;
					}
				}
			}
		}
	}

	public class HostLogConfiguration
	{
		public bool Console { get; set; }

		public bool ErrorLogsToTelegram { get; set; }

		public string PathFormat { get; set; }

		public string MinimumLevel { get; set; }

		public string DbMinimumLevel { get; set; }

		public bool EnableEntityFrameworkLogging { get; set; }
	}

	public class DatabaseConfiguration : UlearnConfigurationBase
	{
		public string Database { get; set; }
	}

	public class UlearnConfiguration : UlearnConfigurationBase
	{
		public TelegramConfiguration Telegram { get; set; }

		[CanBeNull]
		public string BaseUrl { get; set; }

		public string CoursesDirectory { get; set; }

		public string ExerciseStudentZipsDirectory { get; set; }
		
		public string ExerciseCheckerZipsDirectory { get; set; }
		
		public bool ExerciseCheckerZipsCacheDisabled { get; set; }

		public CertificateConfiguration Certificates { get; set; }

		public string GraphiteServiceName { get; set; }

		public string Database { get; set; }

		public GitConfiguration Git { get; set; }

		public string StatsdConnectionString { get; set; } // ConnectionString для подключения к Graphite-relay в формате "address=graphite-relay.com;port=8125;prefixKey=ulearn.local". Можно оставить пустой, чтобы не отправлять метрики

		public string SubmissionsUrl { get; set; } // Url to Ulearn.Web instance. I.E. https://ulearn.me

		public string RunnerToken { get; set; } // Must be equal on Ulearn.Web and RunC***Job instance

		public int? KeepAliveInterval { get; set; }
		
		public HostLogConfiguration HostLog { get; set; }

		public int? Port { get; set; }

		public bool? ForceHttps { get; set; }

		public string Environment { get; set; }

		public HerculesSinkConfiguration Hercules { get; set; }

		[CanBeNull] public AntiplagiarismClientConfiguration AntiplagiarismClient { get; set; }

		[CanBeNull] public VideoAnnotationsClientConfiguration VideoAnnotationsClient { get; set; }
		
		[CanBeNull] public NotificationsConfiguration Notifications { get; set; }
		
		[CanBeNull] public XQueueWatcherConfiguration XQueueWatcher { get; set; }
		
		public bool DisableKonturServices { get; set; }
	}

	public class VideoAnnotationsClientConfiguration
	{
		public string Endpoint { get; set; }
	}

	public class AntiplagiarismClientConfiguration
	{
		public bool Enabled { get; set; }
		public string Endpoint { get; set; }
		public string Token { get; set; }
	}

	public class NotificationsConfiguration
	{
		public bool Enabled { get; set; }
	}
	
	public class XQueueWatcherConfiguration
	{
		public bool Enabled { get; set; }
	}

	public class HerculesSinkConfiguration
	{
		public string ApiKey { get; set; }
		public string Stream { get; set; }
	}

	public class TelegramConfiguration
	{
		public string BotToken { get; set; }

		public ErrorsTelegramConfiguration Errors { get; set; }
	}

	public class ErrorsTelegramConfiguration
	{
		public string Channel { get; set; }
	}

	public class CertificateConfiguration
	{
		public string Directory { get; set; }
	}

	public class GitConfiguration
	{
		public GitWebhookConfiguration Webhook { get; set; }
	}

	public class GitWebhookConfiguration
	{
		public string Secret { get; set; }
	}
}