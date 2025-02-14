using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Applications;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.History;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.IndexerVersions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Update.Commands;

namespace NzbDrone.Core.Jobs
{
    public interface ITaskManager
    {
        IList<ScheduledTask> GetPending();
        List<ScheduledTask> GetAll();
        DateTime GetNextExecution(Type type);
    }

    public class TaskManager : ITaskManager, IHandle<ApplicationStartedEvent>, IHandle<CommandExecutedEvent>
    {
        private readonly IScheduledTaskRepository _scheduledTaskRepository;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly ICached<ScheduledTask> _cache;

        public TaskManager(IScheduledTaskRepository scheduledTaskRepository, IConfigService configService, ICacheManager cacheManager, Logger logger)
        {
            _scheduledTaskRepository = scheduledTaskRepository;
            _configService = configService;
            _cache = cacheManager.GetCache<ScheduledTask>(GetType());
            _logger = logger;
        }

        public IList<ScheduledTask> GetPending()
        {
            return _cache.Values
                         .Where(c => c.Interval > 0 && c.LastExecution.AddMinutes(c.Interval) < DateTime.UtcNow)
                         .ToList();
        }

        public List<ScheduledTask> GetAll()
        {
            return _cache.Values.ToList();
        }

        public DateTime GetNextExecution(Type type)
        {
            var scheduledTask = _cache.Find(type.FullName);

            return scheduledTask.LastExecution.AddMinutes(scheduledTask.Interval);
        }

        public void Handle(ApplicationStartedEvent message)
        {
            var defaultTasks = new List<ScheduledTask>
                {
                    new ScheduledTask { Interval = 5, TypeName = typeof(MessagingCleanupCommand).FullName },
                    new ScheduledTask { Interval = 6 * 60, TypeName = typeof(ApplicationCheckUpdateCommand).FullName },
                    new ScheduledTask { Interval = 6 * 60, TypeName = typeof(CheckHealthCommand).FullName },
                    new ScheduledTask { Interval = 24 * 60, TypeName = typeof(HousekeepingCommand).FullName },
                    new ScheduledTask { Interval = 24 * 60, TypeName = typeof(CleanUpHistoryCommand).FullName },
                    new ScheduledTask { Interval = 24 * 60, TypeName = typeof(IndexerDefinitionUpdateCommand).FullName },
                    new ScheduledTask { Interval = 6 * 60, TypeName = typeof(ApplicationIndexerSyncCommand).FullName },

                    new ScheduledTask
                    {
                        Interval = GetBackupInterval(),
                        TypeName = typeof(BackupCommand).FullName
                    }
                };

            var currentTasks = _scheduledTaskRepository.All().ToList();

            _logger.Trace("Initializing jobs. Available: {0} Existing: {1}", defaultTasks.Count, currentTasks.Count);

            foreach (var job in currentTasks)
            {
                if (!defaultTasks.Any(c => c.TypeName == job.TypeName))
                {
                    _logger.Trace("Removing job from database '{0}'", job.TypeName);
                    _scheduledTaskRepository.Delete(job.Id);
                }
            }

            foreach (var defaultTask in defaultTasks)
            {
                var currentDefinition = currentTasks.SingleOrDefault(c => c.TypeName == defaultTask.TypeName) ?? defaultTask;

                currentDefinition.Interval = defaultTask.Interval;

                if (currentDefinition.Id == 0)
                {
                    currentDefinition.LastExecution = DateTime.UtcNow;
                }

                currentDefinition.Priority = defaultTask.Priority;

                _cache.Set(currentDefinition.TypeName, currentDefinition);
                _scheduledTaskRepository.Upsert(currentDefinition);
            }
        }

        private int GetBackupInterval()
        {
            var interval = _configService.BackupInterval;

            return interval * 60 * 24;
        }

        public void Handle(CommandExecutedEvent message)
        {
            var scheduledTask = _scheduledTaskRepository.All().SingleOrDefault(c => c.TypeName == message.Command.Body.GetType().FullName);

            if (scheduledTask != null && message.Command.Body.UpdateScheduledTask)
            {
                _logger.Trace("Updating last run time for: {0}", scheduledTask.TypeName);

                var lastExecution = DateTime.UtcNow;

                _scheduledTaskRepository.SetLastExecutionTime(scheduledTask.Id, lastExecution, message.Command.StartedAt.Value);
                _cache.Find(scheduledTask.TypeName).LastExecution = lastExecution;
                _cache.Find(scheduledTask.TypeName).LastStartTime = message.Command.StartedAt.Value;
            }
        }

        public void HandleAsync(ConfigSavedEvent message)
        {
            var backup = _scheduledTaskRepository.GetDefinition(typeof(BackupCommand));
            backup.Interval = GetBackupInterval();

            _scheduledTaskRepository.UpdateMany(new List<ScheduledTask> { backup });

            _cache.Find(backup.TypeName).Interval = backup.Interval;
        }
    }
}
