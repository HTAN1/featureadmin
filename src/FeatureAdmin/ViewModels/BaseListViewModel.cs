﻿using Caliburn.Micro;
using FeatureAdmin.Core.Messages;
using FeatureAdmin.Core.Models;
using FeatureAdmin.Messages;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System;
using FeatureAdmin.Core.Models.Enums;
using System.Linq.Expressions;
using System.Windows;

namespace FeatureAdmin.ViewModels
{
    public abstract class BaseListViewModel<T> : Conductor<T>.Collection.OneActive, IHandle<SetSearchFilter<T>> where T : class, IBaseItem
    {
        protected IEventAggregator eventAggregator;
        protected DateTime lastUpdateInitiatedSearch;
        private string searchInput;

        private Scope? selectedScopeFilter;

        public BaseListViewModel(IEventAggregator eventAggregator)
        {
            this.eventAggregator = eventAggregator;
            this.eventAggregator.Subscribe(this);
            ScopeFilters = new ObservableCollection<Scope>(Common.Constants.Search.ScopeFilterList);
            allItems = new ObservableCollection<T>();

            lastUpdateInitiatedSearch = DateTime.Now;
        }

        public ObservableCollection<Scope> ScopeFilters { get; private set; }

        public string SearchInput
        {
            get { return searchInput; }
            set
            {
                searchInput = value;
                FilterResults();
            }
        }

        // The filtered Items
        protected ObservableCollection<T> allItems { get; private set; }
        public Scope? SelectedScopeFilter
        {
            get { return selectedScopeFilter; }
            set
            {
                selectedScopeFilter = value;
                FilterResults();
            }
        }

        public void CopyToClipBoard(string textToCopy)
        {
            if (!string.IsNullOrEmpty(textToCopy))
            {
                Clipboard.SetText(textToCopy);
            }
        }

        public void FilterFeature()
        {
            var searchFilter = new SetSearchFilter<FeatureDefinition>(

                ActiveItem == null ? string.Empty : ActiveItem.Id.ToString(), null);
            eventAggregator.BeginPublishOnUIThread(searchFilter);
        }

        public void FilterLocation()
        {
            var searchFilter = new SetSearchFilter<Location>(
                ActiveItem == null ? string.Empty : ActiveItem.Id.ToString(), null);
            eventAggregator.BeginPublishOnUIThread(searchFilter);
        }

        public void FilterThis()
        {
            var searchFilter = new SetSearchFilter<T>(
                ActiveItem == null ? string.Empty : ActiveItem.Id.ToString(), null);
            Handle(searchFilter);
        }

        
        public void Handle(SetSearchFilter<T> message)
        {
            if (message == null)
            {
                return;
            };

            if (message.SetQuery)
            {
                SearchInput = message.SearchQuery;
            }

            if (message.SetScope)
            {
                SelectedScopeFilter = message.SearchScope;
            }

        }

        protected void FilterResults()
        {
            IEnumerable<T> searchResult;

            if (string.IsNullOrEmpty(searchInput))
            {
                searchResult = allItems;
            }
            else
            {
                Guid idGuid;
                Guid.TryParse(searchInput, out idGuid);

                // if searchInput is not a guid, seachstring will always be a guid.empty
                // to also catch, if user intentionally wants to search for guid empty, this is checked here, too
                if (searchInput.Equals(Guid.Empty.ToString()) || idGuid != Guid.Empty)
                {
                    searchResult = allItems.Where(GetSearchForGuid(idGuid));
                }
                else
                {
                    var lowerCaseSearchInput = searchInput.ToLower();
                    searchResult =
                       allItems.Where(GetSearchForString(lowerCaseSearchInput));
                }

            }

            if (SelectedScopeFilter != null)
            {
                searchResult =
                    searchResult.Where(l => l.Scope == SelectedScopeFilter.Value);
            }

            var activeItemCache = ActiveItem;

            Items.Clear();
            Items.AddRange(searchResult);

            if (activeItemCache != null && Items.Contains(activeItemCache))
            {
                ActivateItem(activeItemCache);
            }

        }

        // Searching for results can be different in derived types
        protected abstract Func<T, bool> GetSearchForGuid(Guid guid);


        // Searching for results can be different in derived types
        protected abstract Func<T, bool> GetSearchForString(string searchString);
    }
}