﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Database;
using Database.DataContexts;
using Database.Models;
using Graphite;
using Vostok.Logging.Abstractions;
using Ulearn.Core.Configuration;
using Ulearn.Core.Logging;
using Ulearn.Core.Metrics;

namespace Notifications
{
	public class Program
	{
		private ULearnDb db;
		private NotificationsRepo notificationsRepo;
		public volatile bool Stopped;
		private readonly WebCourseManager courseManager;
		private readonly NotificationSender notificationSender;

		private readonly ServiceKeepAliver keepAliver;
		private readonly TimeSpan keepAliveInterval;

		private static ILog log => LogProvider.Get().ForContext(typeof(Program));

		public Program(WebCourseManager courseManager)
		{
			this.courseManager = courseManager;
			notificationSender = new NotificationSender(courseManager);
			var configuration = ApplicationConfiguration.Read<UlearnConfiguration>();
			keepAliver = new ServiceKeepAliver(configuration.GraphiteServiceName);
			keepAliveInterval = TimeSpan.FromSeconds(configuration.KeepAliveInterval ?? 30);
		}

		public Program()
			: this(WebCourseManager.Instance)
		{
		}

		static void Main(string[] args)
		{
			var configuration = ApplicationConfiguration.Read<UlearnConfiguration>();
			LoggerSetup.Setup(configuration.HostLog, configuration.GraphiteServiceName);
			/* Pass first argument 'send' to send emails to addresses from `emails.txt` with content from `content.txt` (notifications daemon is not started in this case)*/
			if (args.Length > 0 && args[0] == "send")
			{
				var sender = new OneTimeEmailSender();
				sender.SendEmailsAsync().Wait();
				return;
			}

			if (configuration.Notifications != null && configuration.Notifications.Enabled == false)
			{
				Thread.Sleep(Timeout.Infinite);
				return;
			}

			new Program().MainLoop().Wait();
		}

		public async Task MainLoop()
		{
			StaticMetricsPipeProvider.Instance.Start();

			Stopped = false;
			while (!Stopped)
			{
				db = new ULearnDb();
				notificationsRepo = new NotificationsRepo(db);

				try
				{
					await CreateDeliveries().ConfigureAwait(false);
					await SendDeliveries().ConfigureAwait(false);
				}
				catch (Exception e)
				{
					log.Error(e, "Can\'t create deliveries or send them");
					log.Info("Waiting one second and repeat");
				}

				keepAliver.Ping(keepAliveInterval);
				await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
			}

			StaticMetricsPipeProvider.Instance.Stop();
		}

		private async Task CreateDeliveries()
		{
			await notificationsRepo.CreateDeliveries();
		}

		private async Task SendDeliveries()
		{
			var deliveries = notificationsRepo.GetDeliveriesForSendingNow();

			var deliveryGroups = deliveries.GroupBy(d => Tuple.Create(d.NotificationTransportId, d.Notification.GetNotificationType()));
			foreach (var deliveryKvp in deliveryGroups)
			{
				var deliveriesForTransport = deliveryKvp.ToList();
				if (deliveriesForTransport.Count <= 0)
					continue;

				var transport = deliveriesForTransport[0].NotificationTransport;
				var notificationType = deliveryKvp.Key.Item2;
				await SendDeliveriesOfOneType(transport, notificationType, deliveriesForTransport);
			}
		}

		public async Task SendDeliveriesOfOneType(NotificationTransport transport, NotificationType notificationType, List<NotificationDelivery> deliveries)
		{
			if (deliveries.Count == 0)
				return;

			if (deliveries.Count == 1)
			{
				var delivery = deliveries[0];
				var course = courseManager.FindCourse(delivery.Notification.CourseId);
				if (course == null)
				{
					log.Warn($"Can't find course {delivery.Notification.CourseId}");
					await notificationsRepo.MarkDeliveryAsFailed(delivery);
					return;
				}

				try
				{
					await notificationSender.SendAsync(delivery);
					await notificationsRepo.MarkDeliveryAsSent(delivery.Id);
				}
				catch (Exception e)
				{
					log.Warn(e, $"Can\'t send notification {delivery.NotificationId} to {delivery.NotificationTransport}. Will try later");
					await notificationsRepo.MarkDeliveryAsFailed(delivery);
				}
			}
			else
			{
				try
				{
					await notificationSender.SendAsync(deliveries);
					await notificationsRepo.MarkDeliveriesAsSent(deliveries.Select(d => d.Id).ToList());
				}
				catch (Exception e)
				{
					log.Warn(e, $"Can\'t send multiple notifications [{string.Join(", ", deliveries.Select(d => d.NotificationId))}] to {deliveries[0].NotificationTransport}. Will try later");
					await notificationsRepo.MarkDeliveriesAsFailed(deliveries);
				}
			}
		}
	}
}