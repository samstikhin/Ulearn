using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Database.Extensions;
using Database.Models;
using Vostok.Logging.Abstractions;
using Ulearn.Common;
using Ulearn.Common.Extensions;
using Ulearn.Core.Courses;

namespace Database.DataContexts
{
	public class NotificationsRepo
	{
		private const int maxNotificationsSendingFails = 15;
		public static TimeSpan sendNotificationsDelayAfterCreating = TimeSpan.FromMinutes(1);

		private static ILog log => LogProvider.Get().ForContext(typeof(NotificationsRepo));

		private readonly ULearnDb db;
		private readonly UnitsRepo unitsRepo;
		private readonly UserRolesRepo userRolesRepo;
		private readonly UsersRepo usersRepo;
		private readonly WebCourseManager courseManager;

		public NotificationsRepo()
			: this(new ULearnDb())
		{
		}

		public NotificationsRepo(ULearnDb db)
		{
			this.db = db;
			unitsRepo = new UnitsRepo(db);
			userRolesRepo = new UserRolesRepo(db);
			usersRepo = new UsersRepo(db);
			courseManager = WebCourseManager.Instance;
		}

		private static DateTime CalculateNextTryTime(DateTime createTime, int failsCount)
		{
			if (failsCount == 0)
				return createTime;
			return createTime.AddSeconds(Math.Pow(2, failsCount - 1));
		}

		private static List<NotificationType> notificationTypesCache;

		private static void BuildNotificationTypesCache()
		{
			if (notificationTypesCache != null)
				return;

			notificationTypesCache = Enum.GetValues(typeof(NotificationType)).Cast<NotificationType>().ToList();
		}

		public static List<NotificationType> GetAllNotificationTypes()
		{
			BuildNotificationTypesCache();
			return notificationTypesCache;
		}

		private void DeleteOldNotificationTransports<TTransport>(string userId) where TTransport : NotificationTransport
		{
			/*
			var transports = db.NotificationTransports
				.Where(t => t.UserId == userId && !t.IsDeleted)
				.OfType<TTransport>().ToList();
			foreach (var transport in transports)
				transport.IsDeleted = true;
			*/
			DeleteOldNotificationTransports(typeof(TTransport), userId);
		}

		private void DeleteOldNotificationTransports(Type transportType, string userId)
		{
			if (!typeof(NotificationTransport).IsAssignableFrom(transportType))
				throw new ArgumentException("Parameter 'transportType' is not a NotificationTransport", nameof(transportType));

			var transports = db.NotificationTransports
				.Where(t => t.UserId == userId && !t.IsDeleted)
				.DynamicOfType(transportType)
				.ToList();
			foreach (var transport in transports)
				transport.IsDeleted = true;
		}

		public async Task AddNotificationTransport(NotificationTransport transport)
		{
			DeleteOldNotificationTransports(transport.GetType(), transport.UserId);

			transport.IsDeleted = false;
			db.NotificationTransports.Add(transport);

			await db.SaveChangesAsync();
		}

		public NotificationTransport FindNotificationTransport(int transportId)
		{
			return db.NotificationTransports.Find(transportId);
		}

		public async Task EnableNotificationTransport(int transportId, bool isEnabled = true)
		{
			var transport = db.NotificationTransports.Find(transportId);
			if (transport == null)
				return;

			transport.IsEnabled = isEnabled;
			await db.SaveChangesAsync();
		}

		public List<NotificationTransport> GetUsersNotificationTransports(string userId, bool includeDisabled = false)
		{
			var transports = db.NotificationTransports.Where(t => t.UserId == userId && !t.IsDeleted);
			if (!includeDisabled)
				transports = transports.Where(r => r.IsEnabled);
			return transports.ToList();
		}

		public T FindUsersNotificationTransport<T>(string userId, bool includeDisabled = false) where T : NotificationTransport
		{
			return GetUsersNotificationTransports(userId, includeDisabled).OfType<T>().FirstOrDefault();
		}

		public async Task SetNotificationTransportSettings(string courseId, int transportId, NotificationType type, bool isEnabled)
		{
			var settings = db.NotificationTransportSettings.FirstOrDefault(
				s => s.CourseId == courseId && s.NotificationTransportId == transportId && s.NotificationType == type
			);
			if (settings == null)
			{
				settings = new NotificationTransportSettings
				{
					CourseId = courseId,
					NotificationTransportId = transportId,
					NotificationType = type,
					IsEnabled = isEnabled,
				};
			}
			else
				settings.IsEnabled = isEnabled;

			db.NotificationTransportSettings.AddOrUpdate(settings);
			await db.SaveChangesAsync();
		}

		public DefaultDictionary<int, NotificationTransportSettings> GetNotificationTransportsSettings(string courseId, NotificationType type, List<int> transportIds)
		{
			return db.NotificationTransportSettings
				.Where(s => s.CourseId == courseId && s.NotificationType == type && transportIds.Contains(s.NotificationTransportId))
				.ToList()
				.ToDictSafe(s => s.NotificationTransportId, s => s)
				.ToDefaultDictionary(() => null);
		}

		// Dictionary<(notificationTransportId, NotificationType), NotificationTransportSettings>
		public DefaultDictionary<Tuple<int, NotificationType>, NotificationTransportSettings> GetNotificationTransportsSettings(string courseId, List<int> transportIds)
		{
			return db.NotificationTransportSettings
				.Where(s => s.CourseId == courseId && transportIds.Contains(s.NotificationTransportId))
				.ToList()
				.ToDictSafe(s => Tuple.Create(s.NotificationTransportId, s.NotificationType), s => s)
				.ToDefaultDictionary(() => null);
		}

		// Dictionary<(notificationTransportId, NotificationType), NotificationTransportSettings>
		public DefaultDictionary<Tuple<int, NotificationType>, NotificationTransportSettings> GetNotificationTransportsSettings(string courseId, string userId)
		{
			return db.NotificationTransportSettings
				.Include(s => s.NotificationTransport)
				.Where(s => s.CourseId == courseId && s.NotificationTransport.UserId == userId)
				.ToList()
				.ToDictSafe(s => Tuple.Create(s.NotificationTransportId, s.NotificationType), s => s)
				.ToDefaultDictionary(() => null);
		}

		public async Task AddNotification(string courseId, Notification notification, string initiatedUserId)
		{
			notification.CreateTime = DateTime.Now;
			notification.InitiatedById = initiatedUserId;
			notification.CourseId = courseId;
			db.Notifications.Add(notification);

			await db.SaveChangesAsync();
		}

		public async Task RemoveNotifications(Guid courseVersionId)
		{
			// Cascade delete not work: multiple cascade paths
			var forRemove = db.Notifications.OfType<UploadedPackageNotification>().Where(n => n.CourseVersionId == courseVersionId).Cast<AbstractPackageNotification>()
				.Concat(db.Notifications.OfType<PublishedPackageNotification>().Where(n => n.CourseVersionId == courseVersionId)).ToList();

			if (forRemove.Count == 0)
				return;

			db.Notifications.RemoveRange(forRemove);

			await db.SaveChangesAsync();
		}

		public List<NotificationDelivery> GetDeliveriesForSendingNow()
		{
			var now = DateTime.Now;
			return db.NotificationDeliveries.Where(
				d => (d.NextTryTime < now || d.NextTryTime == null) &&
					d.Status == NotificationDeliveryStatus.NotSent &&
					d.FailsCount < maxNotificationsSendingFails
			).ToList();
		}

		public async Task MarkDeliveriesAsSent(List<int> deliveriesIds)
		{
			foreach (var d in db.NotificationDeliveries.Where(d => deliveriesIds.Contains(d.Id)))
				d.Status = NotificationDeliveryStatus.Sent;
			await db.SaveChangesAsync();
		}

		private async Task SetDeliveryStatus(int deliveryId, NotificationDeliveryStatus status)
		{
			var delivery = db.NotificationDeliveries.Find(deliveryId);
			if (delivery == null)
				return;

			delivery.Status = status;
			await db.SaveChangesAsync();
		}

		public async Task MarkDeliveryAsRead(int deliveryId)
		{
			await SetDeliveryStatus(deliveryId, NotificationDeliveryStatus.Read);
		}

		public async Task MarkDeliveryAsWontSend(int deliveryId)
		{
			await SetDeliveryStatus(deliveryId, NotificationDeliveryStatus.WontSend);
		}

		public async Task MarkDeliveryAsSent(int deliveryId)
		{
			await SetDeliveryStatus(deliveryId, NotificationDeliveryStatus.Sent);
		}

		public async Task CreateDeliveries()
		{
			var minuteAgo = DateTime.Now.Subtract(sendNotificationsDelayAfterCreating);
			var notifications = db.Notifications.Where(
				n => !n.AreDeliveriesCreated && n.CreateTime < minuteAgo
			).OrderBy(n => n.Id).ToList();
			foreach (var notification in notifications)
			{
				try
				{
					CreateDeliviesForNotification(notification);
				}
				finally
				{
					notification.AreDeliveriesCreated = true;
				}

				await db.SaveChangesAsync();
			}
		}

		private void CreateDeliviesForNotification(Notification notification)
		{
			var notificationType = notification.GetNotificationType();
			log.Info($"Found new notification {notificationType} #{notification.Id}");

			if (!notification.IsActual())
			{
				log.Info($"Notification #{notification.Id}: is not actual more");
				return;
			}

			var blockerNotifications = notification.GetBlockerNotifications(db);
			if (blockerNotifications.Count > 0)
			{
				log.Info(
					$"There are blocker notifications (this one will not be send if blocker notifications has been already sent to the same transport): " +
					$"{string.Join(", ", blockerNotifications.Select(n => $"{n.GetNotificationType()} #{n.Id}"))}"
				);
			}

			Course course = null;
			if (!string.IsNullOrWhiteSpace(notification.CourseId))
			{
				course = courseManager.FindCourse(notification.CourseId);
				if (course == null)
					return;
			}

			var recipientsIds = notification.GetRecipientsIds(db, course).ToHashSet();

			recipientsIds = FilterUsersWhoNotSeeCourse(notification, recipientsIds);
			
			log.Info($"Recipients list for notification {notification.Id}: {recipientsIds.Count} user(s)");

			if (recipientsIds.Count == 0)
				return;

			if (recipientsIds.Count > 1000)
			{
				log.Warn($"Recipients list for notification is too big {notification.Id}: {recipientsIds.Count} user(s)");
			}

			var transportsSettings = recipientsIds.Count > 1000 ?
				db.NotificationTransportSettings
					.Include(s => s.NotificationTransport)
					.Where(s => s.CourseId == notification.CourseId &&
								s.NotificationType == notificationType &&
								!s.NotificationTransport.IsDeleted)
					.ToList()
					.Where(s => recipientsIds.Contains(s.NotificationTransport.UserId))
					.ToList()
				: db.NotificationTransportSettings
					.Include(s => s.NotificationTransport)
					.Where(s => s.CourseId == notification.CourseId &&
								s.NotificationType == notificationType &&
								!s.NotificationTransport.IsDeleted &&
								recipientsIds.Contains(s.NotificationTransport.UserId)).ToList();

			var commonTransports = db.NotificationTransports.Where(t => t.UserId == null && t.IsEnabled).ToList();

			var isEnabledByDefault = notificationType.IsEnabledByDefault();

			if (isEnabledByDefault)
			{
				log.Info($"Notification #{notification.Id}. This notification type is enabled by default, so collecting data for it");

				var recipientsTransports = db.NotificationTransports.Where(
					t => recipientsIds.Contains(t.UserId) &&
						!t.IsDeleted &&
						t.IsEnabled
				).ToList();
				var notFoundTransports = recipientsTransports.Except(transportsSettings.Select(c => c.NotificationTransport), new NotificationTransportIdComparer());

				foreach (var transport in notFoundTransports)
				{
					transportsSettings.Add(new NotificationTransportSettings
					{
						IsEnabled = true,
						NotificationTransport = transport,
						NotificationTransportId = transport.Id,
					});
				}
			}
			else if (notification.IsNotificationForEveryone)
			{
				/* Add notification to all common transports */
				/* It's used i.e. for new-comment notification which should be sent to everyone in the course */
				log.Info($"Notification #{notification.Id}. This notification type is sent to everyone, so add it to all common transports ({commonTransports.Count} transport(s)):");
				foreach (var commonTransport in commonTransports)
				{
					log.Info($"Common transport {commonTransport}");
					transportsSettings.Add(new NotificationTransportSettings
					{
						IsEnabled = true,
						NotificationTransport = commonTransport,
						NotificationTransportId = commonTransport.Id,
					});
				}
			}

			var blockerNotificationsSentToTransports = new HashSet<int>(
				GetNotificationsDeliveries(blockerNotifications.Select(n => n.Id), transportsSettings.Select(s => s.NotificationTransportId))
					.Select(d => d.NotificationTransportId)
			);
			if (blockerNotificationsSentToTransports.Count > 0)
			{
				log.Info(
					"Blocked notifications have been already sent to follow transports:" +
					$"{string.Join(", ", blockerNotificationsSentToTransports)}"
				);
			}

			var now = DateTime.Now;

			foreach (var transportSettings in transportsSettings)
			{
				log.Info($"Notification #{notification.Id}: add delivery to {transportSettings.NotificationTransport}, isEnabled: {transportSettings.IsEnabled}");
				if (!transportSettings.IsEnabled)
					continue;

				if (!transportSettings.NotificationTransport.IsEnabled)
				{
					log.Info($"Transport {transportSettings.NotificationTransport} is fully disabled, ignore it");
					continue;
				}

				/* Always ignore to send notification to user initiated this notification */
				if (transportSettings.NotificationTransport.UserId == notification.InitiatedById)
				{
					log.Info($"Don't sent notification to the transport {transportSettings.NotificationTransport} because it has been initiated by this user");
					continue;
				}

				if (blockerNotificationsSentToTransports.Contains(transportSettings.NotificationTransportId))
				{
					log.Info($"Some blocker notification already has been sent to transport {transportSettings.NotificationTransport}, ignore it");
					continue;
				}

				log.Info($"Notification #{notification.Id}: add delivery to {transportSettings.NotificationTransport}, sending at {now}");
				db.NotificationDeliveries.Add(new NotificationDelivery
				{
					Notification = notification,
					NotificationTransportId = transportSettings.NotificationTransportId,
					CreateTime = now,
					NextTryTime = now,
					FailsCount = 0,
					Status = NotificationDeliveryStatus.NotSent,
				});
			}
		}

		private HashSet<string> FilterUsersWhoNotSeeCourse(Notification notification, HashSet<string> recipientsIds)
		{
			if (notification.CourseId != "")
			{
				var course = courseManager.FindCourse(notification.CourseId);
				if (course != null)
				{
					var visibleUnits = unitsRepo.GetVisibleUnitIds(course);
					if (!visibleUnits.Any())
					{
						var userIdsWithInstructorRoles = userRolesRepo.GetListOfUsersWithCourseRole(CourseRole.Tester, notification.CourseId, true);
						var sysAdminsIds = usersRepo.GetSysAdminsIds();
						recipientsIds.IntersectWith(userIdsWithInstructorRoles.Concat(sysAdminsIds));
					}
				}
			}
			return recipientsIds;
		}

		private IQueryable<NotificationDelivery> GetNotificationsDeliveries(IEnumerable<int> notificationsIds, IEnumerable<int> transportsIds)
		{
			return db.NotificationDeliveries.Where(d => notificationsIds.Contains(d.NotificationId) && transportsIds.Contains(d.NotificationTransportId));
		}

		public async Task MarkDeliveriesAsFailed(IEnumerable<NotificationDelivery> deliveries)
		{
			foreach (var delivery in deliveries)
			{
				delivery.FailsCount++;
				delivery.NextTryTime = CalculateNextTryTime(delivery.CreateTime, delivery.FailsCount);
			}

			await db.SaveChangesAsync();
		}

		public Task MarkDeliveryAsFailed(NotificationDelivery delivery)
		{
			return MarkDeliveriesAsFailed(new List<NotificationDelivery> { delivery });
		}

		public string GetSecretHashForTelegramTransport(long chatId, string chatTitle, string key)
		{
			var hasher = MD5.Create();
			var bytes = Encoding.UTF8.GetBytes(key + chatId + "&" + chatTitle + key);
			var hash = hasher.ComputeHash(bytes);

			var sb = new StringBuilder();
			foreach (var b in hash)
				sb.Append(b.ToString("x2"));
			return sb.ToString();
		}

		public List<NotificationType> GetNotificationTypes(string userId, string courseId)
		{
			var notificationTypes = GetAllNotificationTypes();

			var courseRole = userRolesRepo.GetRole(userId, courseId);
			notificationTypes = notificationTypes
				.Where(t => t.GetMinCourseRole() <= courseRole)
				.OrderByDescending(t => t.GetMinCourseRole())
				.ThenBy(t => (int)t)
				.ToList();

			if (!userRolesRepo.IsSystemAdministrator(userId))
				notificationTypes = notificationTypes.Where(t => !t.IsForSysAdminsOnly()).ToList();

			return notificationTypes;
		}

		public static string GetNotificationTransportEnablingSignature(int transportId, long timestamp, string secret)
		{
			return $"{secret}transport={transportId}&timestamp={timestamp}{secret}".CalculateMd5();
		}

		public List<T> FindNotifications<T>(Expression<Func<T, bool>> func, Expression<Func<T, object>> includePath = null) where T : Notification
		{
			var query = db.Notifications.OfType<T>();
			if (includePath != null)
				query = query.Include(includePath);
			return query.Where(func).ToList();
		}

		public IQueryable<NotificationDelivery> GetTransportDeliveries(NotificationTransport notificationTransport, DateTime from)
		{
			return db.NotificationDeliveries.Where(d => d.NotificationTransportId == notificationTransport.Id && d.CreateTime > from);
		}

		public IQueryable<NotificationDelivery> GetTransportsDeliveries(List<int> notificationTransportsIds, DateTime from)
		{
			return db.NotificationDeliveries.Include(d => d.Notification).Where(d => notificationTransportsIds.Contains(d.NotificationTransportId) && d.CreateTime > from);
		}

		public DateTime? GetLastDeliveryTimestamp(NotificationTransport notificationTransport)
		{
			var lastNotification = db.NotificationDeliveries.Where(d => d.NotificationTransportId == notificationTransport.Id)
				.OrderByDescending(d => d.CreateTime)
				.FirstOrDefault();
			return lastNotification?.CreateTime;
		}
	}

	class NotificationTransportIdComparer : IEqualityComparer<NotificationTransport>
	{
		public bool Equals(NotificationTransport first, NotificationTransport second)
		{
			return first.Id == second.Id;
		}

		public int GetHashCode(NotificationTransport transport)
		{
			return transport.Id.GetHashCode();
		}
	}
}