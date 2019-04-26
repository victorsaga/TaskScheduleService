using Microsoft.Extensions.Configuration;
using NLog;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Topshelf;
using Topshelf.HostConfigurators;

namespace ConsoleApp1
{
    class Program
    {
        //掛到service時的啟動路徑會是C:\Windows\system32，所以要取得exe(dll)的路徑才能正確的讀寫檔案
        public static string assemblyFolder = Path.GetDirectoryName((Assembly.GetEntryAssembly().Location));
        public static void Main(string[] args)
        {
            try
            {
                var host = HostFactory.New(h =>
                {
                    h.Service<ScheduleService>();
                    ConfigureService(h);
                });
                LogManager.GetCurrentClassLogger().Info("Run Run Run");
                host.Run();
            }catch(Exception e)
            {                
                LogManager.GetCurrentClassLogger().Error($"{e.Message}{Environment.NewLine}{e.StackTrace}");
            }
        }

        private static void ConfigureService(HostConfigurator x)
        {
            x.StartAutomatically();
            
            x.SetServiceName(GetSettings().GetValue<string>("ServiceName"));
            x.SetDisplayName(GetSettings().GetValue<string>("ServiceName"));                
            x.SetDescription(GetSettings().GetValue<string>("ServiceDescription"));

            x.EnableServiceRecovery(r =>
            {
                r.RestartService(0);
                r.OnCrashOnly();
                r.SetResetPeriod(1);
            });
        }

        public static IConfigurationRoot GetSettings()
        {
            return new ConfigurationBuilder()
                .SetBasePath(assemblyFolder)
                .AddJsonFile("appsettings.json", false)
                .Build();
        }
    }

    class ScheduleService : ServiceControl
    {
        private static IScheduler scheduler;
        private static IList<ScheduleModel> schedules;
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        public ScheduleService()
        {
            NameValueCollection props = new NameValueCollection
            {
                { "quartz.serializer.type", "binary" },
                { "quartz.scheduler.instanceName", "MyScheduler" },
                { "quartz.jobStore.type", "Quartz.Simpl.RAMJobStore, Quartz" },
                { "quartz.threadPool.threadCount", "3" }
            };
            StdSchedulerFactory factory = new StdSchedulerFactory(props);
            scheduler = factory.GetScheduler().ConfigureAwait(false).GetAwaiter().GetResult();
        }
        public bool Start(HostControl hostControl)
        {
            scheduler.Start().ConfigureAwait(false).GetAwaiter().GetResult();
            
            MainScheduleJobs();

            return true;
        }
        private void MainScheduleJobs()
        {
            int SchedulesRefreshSeconds = Program.GetSettings().GetValue<int>("SchedulesRefreshSeconds");
            logger.Info($"Add MainScheduleJobs SchedulesRefreshSeconds = {SchedulesRefreshSeconds}");

            IJobDetail job = JobBuilder.Create<MainJob>()
                .WithIdentity("MainJob", "MainGroup")
                .Build();

            ITrigger trigger = TriggerBuilder.Create()
                .WithIdentity("MainTrigger", "MainGroup")
                .StartNow()
                .WithSimpleSchedule(x => x
                    .WithIntervalInSeconds(SchedulesRefreshSeconds)
                    .RepeatForever())
                .Build();

            scheduler.ScheduleJob(job, trigger);
        }
        public bool Stop(HostControl hostControl)
        {
            scheduler.Shutdown().ConfigureAwait(false).GetAwaiter().GetResult();

            return true;
        }

        [DisallowConcurrentExecutionAttribute]
        private class MainJob : IJob
        {
            public Task Execute(IJobExecutionContext context)
            {
                string subJobGroupName = "subJobGroup";
                string version = DateTime.Now.ToString("_MMdd_HHmmss");

                string logMsg = "******************************** Add Sub Job Version = " + version;
                logger.Info(logMsg);
                Console.WriteLine(logMsg);

                //刪除動態加入的Job                
                scheduler.DeleteJobs( scheduler.GetJobKeys(
                                                                GroupMatcher<JobKey>.GroupEquals(subJobGroupName)
                                                            ).Result );

                //重新從appsettings.json加入Schedules
                schedules = Program.GetSettings().GetSection("Schedules").Get<List<ScheduleModel>>();                
                if (schedules != null && schedules.Count > 0)
                {
                    schedules.Where(x => !string.IsNullOrEmpty(context.JobDetail.Key.Name)
                                            && !string.IsNullOrEmpty(x.WorkingDirectory)
                                            && !string.IsNullOrEmpty(x.FileName)
                                            && !string.IsNullOrEmpty(x.CronExpress)
                                        ).ToList().ForEach(x =>
                    {
                        try
                        {
                            IJobDetail job = JobBuilder.Create<SubJob>()
                                .WithIdentity(x.ScheduleName, subJobGroupName)
                                .Build();

                            ITrigger trigger = TriggerBuilder.Create()
                                .WithIdentity( x.ScheduleName + version, subJobGroupName)
                                .WithCronSchedule(x.CronExpress)
                                .Build();
                            scheduler.ScheduleJob(job, trigger);

                            logMsg = $"Add Sub Job Name = {x.ScheduleName + version}, CornExpress ={x.CronExpress}";
                            logger.Info(logMsg);
                            Console.WriteLine(logMsg);
                        }
                        catch (Exception e)
                        {
                            logger.Error($"Job : {x.ScheduleName??""}, Message: {e.Message}{Environment.NewLine}{e.StackTrace}");
                        }
                    });
                }                
                return Task.CompletedTask;
            }
        }
        
        public class SubJob : IJob
        {
            private static readonly ILogger logger = NLog.LogManager.GetCurrentClassLogger();
            public Task Execute(IJobExecutionContext context)
            {
                string msg = $"***** Start SubJob {context.Trigger.Key}";

                logger.Info(msg);
                Console.WriteLine($"{msg} {DateTime.Now.ToString("HHmmss")}");
                
                if (schedules == null || schedules.Count == 0)
                {
                    logger.Warn($"SubJob's schedules object is null @{context.Trigger.Key}");
                    return Task.CompletedTask;
                }

                var s = schedules.Where(x => x.ScheduleName == context.JobDetail.Key.Name
                                                && !string.IsNullOrEmpty(x.WorkingDirectory)
                                                && !string.IsNullOrEmpty(x.FileName)
                                                && !string.IsNullOrEmpty(x.CronExpress)
                                                ).FirstOrDefault();
                if (s == null)
                {
                    logger.Warn($"SubJob's schedule is INVALID @{context.Trigger.Key}");
                    return Task.CompletedTask;
                }

                try
                {
                    var startInfo = new ProcessStartInfo();
                    startInfo.WorkingDirectory = s.WorkingDirectory;
                    startInfo.FileName = $"{startInfo.WorkingDirectory}\\{s.FileName}";
                    Process proc = Process.Start(startInfo);

                    msg = $"## End Process {context.Trigger.Key}";
                    logger.Info(msg);
                    Console.WriteLine($"{msg} {DateTime.Now.ToString("HHmmss")}");

                }
                catch(Exception e)
                {
                    logger.Error($"Job : {context.Trigger.Key}, Message: {e.Message}{Environment.NewLine}{e.StackTrace}");
                }

                msg = $"##### End SubJob {context.Trigger.Key}";
                logger.Info(msg);
                Console.WriteLine($"{msg} {DateTime.Now.ToString("HHmmss")}");

                return Task.CompletedTask;
            }
        }
    }
    
    public class ScheduleModel
    {
        public string ScheduleName { get; set; }
        public string WorkingDirectory { get; set; }
        public string FileName { get; set; }
        public string CronExpress { get; set; }
    }    
}