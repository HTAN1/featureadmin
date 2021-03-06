﻿using Akka.Actor;
using Akka.DI.Core;
using Akka.Event;
using Caliburn.Micro;
using FeatureAdmin.Actors;
using FeatureAdmin.Core.Messages;
using FeatureAdmin.Core.Messages.Tasks;
using FeatureAdmin.Core.Models.Enums;
using FeatureAdmin.Messages;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FeatureAdmin.Core.Models.Tasks
{
    public class LoadTaskActor : BaseTaskActor
    {
        public ProgressModule Farm;
        public ProgressModule FarmFeatureDefinitions;
        public ProgressModule Preparations;
        public ProgressModule SitesAndWebs;
        public ProgressModule WebApps;

        private readonly ILoggingAdapter _log = Logging.GetLogger(Context);
        private readonly IActorRef featureDefinitionActor;
        private readonly Dictionary<Guid, IActorRef> locationActors;
        
        private FarmFeatureDefinitionsLoaded tempFeatureDefinitionStore = null;
        private List<LocationsLoaded> tempLocationStore = new List<LocationsLoaded>();
        public LoadTaskActor(IEventAggregator eventAggregator, string title, Guid id, Location startLocation)
            : base(eventAggregator, title, id)
        {
            locationActors = new Dictionary<Guid, IActorRef>();

            featureDefinitionActor =
                   Context.ActorOf(Context.DI().Props<FeatureDefinitionActor>());

            Preparations = new ProgressModule(
                5d / 100,
                0d,
                FeatureAdmin.Common.Constants.Tasks.PreparationStepsForLoad);

            FarmFeatureDefinitions = new ProgressModule(
                5d / 100,
                Preparations.MaxCumulatedQuota,
                1);

            Farm = new ProgressModule(
                5d / 100,
                FarmFeatureDefinitions.MaxCumulatedQuota,
                1);
            WebApps = new ProgressModule(
                10d / 100,
                Farm.MaxCumulatedQuota);

            SitesAndWebs = new ProgressModule(
               1d - WebApps.MaxCumulatedQuota,
                WebApps.MaxCumulatedQuota);

            Receive<ClearItemsReady>(message => HandleClearItemsReady(message));
            Receive<LocationsLoaded>(message => HandleLocationsLoaded(message));
            Receive<FarmFeatureDefinitionsLoaded>(message => FarmFeatureDefinitionsLoaded(message));

            InitiateLoadTask(startLocation);
        }

        public override string StatusReport
        {
            get
            {
                return string.Format("'{0}' (ID: '{1}') - Loaded: {2} web apps, {3} sites and webs, {4} features, progress {5:F0}% \nelapsed time: {6}",
                    Title,
                    Id,
                    WebApps.Processed,
                    SitesAndWebs.Processed,
                    FarmFeatureDefinitions.Processed,
                    PercentCompleted * 100,
                    ElapsedTime
                    );
            }
        }

        public override double PercentCompleted
        {
            get
            {
                return Preparations.OuotaPercentage + 
                    FarmFeatureDefinitions.OuotaPercentage +
                    Farm.OuotaPercentage +
                    WebApps.OuotaPercentage +
                    SitesAndWebs.OuotaPercentage;
            }
        }

        /// <summary>
        /// Props provider
        /// </summary>
        /// <param name="eventAggregator"></param>
        /// <param name="title"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        /// <remarks>
        /// see also https://getakka.net/articles/actors/receive-actor-api.html
        /// </remarks>
        public static Props Props(IEventAggregator eventAggregator, string title, Guid id, Location startLocation)
        {
            return Akka.Actor.Props.Create(() => new LoadTaskActor(eventAggregator, title, id, startLocation));
        }

        public void HandleClearItemsReady(ClearItemsReady message)
        {

            // how many steps are expected is decided in Common.Constants.Tasks.PreparationStepsForLoad
            Preparations.Processed++;

            if (Preparations.Completed)
            {
                SendProgress();

                // feature definitions already loaded?
                if (tempFeatureDefinitionStore != null)
                {
                    eventAggregator.BeginPublishOnUIThread(tempFeatureDefinitionStore);
                    tempFeatureDefinitionStore = null;
                }

                // locations already loaded? 
                if (FarmFeatureDefinitions.Completed && tempLocationStore.Count() > 0)
                {
                    foreach (LocationsLoaded loadedMsg in tempLocationStore)
                    {
                        eventAggregator.BeginPublishOnUIThread(loadedMsg);
                    }

                    tempLocationStore.Clear();
                }

            }

        }

        public void TrackLocationProcessed([NotNull] Location location)
        {
            switch (location.Scope)
            {
                case Enums.Scope.Web:
                    SitesAndWebs.Processed++;
                    break;
                case Enums.Scope.Site:
                    SitesAndWebs.Total += location.ChildCount;
                    SitesAndWebs.Processed++;
                    break;
                case Enums.Scope.WebApplication:
                    SitesAndWebs.Total += location.ChildCount;
                    WebApps.Processed++;
                    break;
                case Enums.Scope.Farm:
                    WebApps.Total += location.ChildCount;
                    Farm.Processed++;
                    break;
                case Enums.Scope.ScopeInvalid:
                default:
                    // do not track non valid scopes
                    break;
            }
        }

        public bool TrackLocationsProcessed(LocationsLoaded loadedMessage)
        {
            bool finished = false;

            var parent = loadedMessage.Parent;

            foreach (Location l in loadedMessage.ChildLocations)
            {
                TrackLocationProcessed(l);
            }

            return finished;
        }

        private void FarmFeatureDefinitionsLoaded(FarmFeatureDefinitionsLoaded message)
        {

            FarmFeatureDefinitions.Processed = message.FarmFeatureDefinitions.Count();
            
            if (FarmFeatureDefinitions.Completed)
            {
                SendProgress();

                if (Preparations.Completed)
                {
                    eventAggregator.PublishOnUIThread(message);

                    // locations already loaded? 
                    if (tempLocationStore.Count() > 0)
                    {
                        foreach (LocationsLoaded loadedMsg in tempLocationStore)
                        {
                            eventAggregator.BeginPublishOnUIThread(loadedMsg);
                        }

                        tempLocationStore.Clear();
                    }
                }
                else
                {
                    tempFeatureDefinitionStore = message;
                }


            }
        }

        private void HandleLocationsLoaded([NotNull] LocationsLoaded message)
        {
            TrackLocationsProcessed(message);
#if DEBUG
            var dbgMsg = new FeatureAdmin.Messages.LogMessage(Core.Models.Enums.LogLevel.Debug,
                    string.Format("Debug Load progress: {0}", StatusReport)
                    );
            eventAggregator.PublishOnUIThread(dbgMsg);
#endif
            // if web apps are loaded, load children
            if (message.Parent.Scope == Enums.Scope.Farm)
            {
                foreach (Location l in message.ChildLocations)
                {
                    if (l.Scope == Enums.Scope.WebApplication)
                    {
                        // initiate read of locations
                        var loadWebAppChildren = new LoadLocationQuery(Id, l);
                        LoadTask(loadWebAppChildren);
                    }
                }
            }

            if (Preparations.Completed && FarmFeatureDefinitions.Completed)
            {
                // publish locations to wpf
                eventAggregator.PublishOnUIThread(message);
            }
            else
            {
                tempLocationStore.Add(message);
            }

            SendProgress();
        }

        private void InitiateLoadTask(Location startLocation)
        {
            // initiate clean all feature definition and location collections
            var clearMessage = new ClearItems(Id);
            eventAggregator.PublishOnUIThread(clearMessage);

            // initiate read of all feature definitions
            var fdQuery = new LoadFeatureDefinitionQuery(Id);
            featureDefinitionActor.Tell(fdQuery);

            // initiate read of locations
            var loadQuery = new LoadLocationQuery(Id, startLocation);
            LoadTask(loadQuery);
        }

        private void LoadTask([NotNull] LoadLocationQuery loadQuery)
        {
            _log.Debug("Entered LoadTask");

            var locationId = loadQuery.Location.Id;

            if (!locationActors.ContainsKey(locationId))
            {
                IActorRef newLocationActor =
                    Context.ActorOf(Context.DI().Props<LocationActor>());

                locationActors.Add(locationId, newLocationActor);

                newLocationActor.Tell(loadQuery);
            }
            else
            {
                locationActors[locationId].Tell(loadQuery);
            }
        }
    }
}
