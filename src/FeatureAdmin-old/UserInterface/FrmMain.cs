using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.SharePoint;
using Microsoft.SharePoint.Administration;
using FeatureAdmin.Common;
using FeatureAdmin.Models;

using Serilog;
using FeatureAdmin.Repository;
using FeatureAdmin.Services.SharePointApi;
using System.Linq;

namespace FeatureAdmin.UserInterface
{
    public partial class FrmMain : Form
    {
        #region members

        private FeatureRepository repository = new FeatureRepository();
        private FeatureParent m_CurrentWebAppLocation;
        private FeatureParent m_CurrentSiteLocation;
        private FeatureParent m_CurrentWebLocation;
        private ContextMenuStrip m_featureDefGridContextMenu;
        private FeatureDefinition m_featureDefGridContextFeature;

        private FeatureAdmin.Services.Logger logTextWriter;
        #endregion


        /// <summary>Initialize Main Window</summary>
        public FrmMain()
        {
            InitializeComponent();

            this.Text = Constants.Text.FeatureAdminTitle;

            // initialize logging
            logTextWriter = new Services.Logger(txtResult);

            removeBtnEnabled(false);
            EnableActionButtonsAsAppropriate();

            ConfigureFeatureDefGrid();
            ConfigureWebApplicationsGrid();
            ConfigureSiteCollectionsGrid();
            ConfigureWebsGrid();

            this.Show();

            LoadWebAppGrid(); // Load list of web applications
            ReloadAllFeatureDefinitions(); // Load list of all feature definitions

            EnableActionButtonsAsAppropriate();
        }

        #region FeatureDefinition Methods

        private void ConfigureFeatureDefGrid()
        {
            DataGridView grid = gridFeatureDefinitions;
            grid.AutoGenerateColumns = false;
            GridColMgr.AddTextColumn(grid, "ScopeAbbrev", "Scope", 60);
            GridColMgr.AddTextColumn(grid, "Name");
#if (SP2013)
            GridColMgr.AddTextColumn(grid, "CompatibilityLevel", "Compat", 60);
#endif
            GridColMgr.AddTextColumn(grid, "Version", "Version", 60);
            GridColMgr.AddTextColumn(grid, "Id", 50);
            GridColMgr.AddTextColumn(grid, Common.Constants.PropertyNames.Activations, 60);
            GridColMgr.AddTextColumn(grid, "Faulty", 60);

            GridColMgr.AddTextColumn(grid, Common.Constants.PropertyNames.UpgradesRequired, "Upgrades required", 60);

            // Set most columns sortable
            foreach (DataGridViewColumn column in grid.Columns)
            {
                if (column.DataPropertyName != Common.Constants.PropertyNames.Activations &&
                    column.DataPropertyName != Common.Constants.PropertyNames.UpgradesRequired)
                {
                    column.SortMode = DataGridViewColumnSortMode.Automatic;
                }
            }

            m_featureDefGridContextMenu = UiConfig.CreateFeatureDefinitionContextMenu(gridFeatureDefinitions_ViewActivationsClick);

            // Color faulty rows
            grid.DataBindingComplete += new DataGridViewBindingCompleteEventHandler(gridFeatureDefinitions_DataBindingComplete);
        }

        void gridFeatureDefinitions_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            UiConfig.SetRowRedAndBold(gridFeatureDefinitions.Rows);
        }

     

        /// <summary>
        /// Get list of all feature definitions currently selected in the
        ///  Feature Definition list
        /// </summary>
        private List<FeatureDefinition> GetSelectedFeatureDefinitions()
        {
            var features = new List<FeatureDefinition>();
            foreach (DataGridViewRow row in this.gridFeatureDefinitions.SelectedRows)
            {
                FeatureDefinition feature = row.DataBoundItem as FeatureDefinition;
                features.Add(feature);
            }
            return features;
        }

        /// <summary>Uninstall the selected Feature definition</summary>
        private void btnUninstFDef_Click(object sender, EventArgs e)
        {
            PromptAndUninstallFeatureDefs();
        }
        private void PromptAndUninstallFeatureDefs()
        {
            List<FeatureDefinition> selectedFeatures = GetSelectedFeatureDefinitions();
            if (selectedFeatures.Count != 1)
            {
                MessageBox.Show("Please select exactly 1 feature.");
                return;
            }
            var feature = selectedFeatures[0];

            string msg = string.Format(
                "This will uninstall the {0} selected feature definition(s) from the Farm.",
                selectedFeatures.Count);
            if (!ConfirmBoxOkCancel(msg))
            {
                return;
            }

            msg = "Before uninstalling a feature, it should be deactivated everywhere in the farm.";
            msg += string.Format("\nFeature Scope: {0}", feature.Scope);
            msg += "\nAttempt to remove this feature from everywhere in the farm first?";
            DialogResult choice = MessageBox.Show(msg, "Deactivate Feature", MessageBoxButtons.YesNoCancel);
            if (choice == DialogResult.Cancel)
            {
                return;
            }
            if (choice == DialogResult.Yes)
            {
                RemoveFeaturesWithinFarm(feature.Id, feature.Scope);
            }
            msg = "Use Force flag for uninstall?";
            
            using (WaitCursor wait = new WaitCursor())
            {
                UninstallSelectedFeatureDefinitions(selectedFeatures, repository.AlwaysUseForce);
            }
        }

        #endregion

        #region Feature removal (SiteCollection and SPWeb)

        /// <summary>triggers removeSPWebFeaturesFromCurrentWeb</summary>
        private void btnRemoveFromWeb_Click(object sender, EventArgs e)
        {
            if (clbSPSiteFeatures.CheckedItems.Count > 0)
            {
                MessageBox.Show("Please uncheck all SiteCollection scoped features. Action canceled.",
                    "No SiteCollection scoped Features must be checked");
                return;
            }
            removeSPWebFeaturesFromCurrentWeb();
        }

        /// <summary>Removes selected features from the current web only</summary>
        private void removeSPWebFeaturesFromCurrentWeb()
        {
            if (clbSPWebFeatures.CheckedItems.Count == 0)
            {
                MessageBox.Show(Constants.Text.NOFEATURESELECTED);
                Log.Warning(Constants.Text.NOFEATURESELECTED);
                return;
            }
            if (IsEmpty(m_CurrentWebLocation))
            {
                MessageBox.Show("No web currently selected");
                return;
            }
            if (clbSPSiteFeatures.CheckedItems.Count > 0) { throw new Exception("Mixed mode unsupported"); }

            string msgString = string.Format(
                "This will force deactivate the {0} selected feature(s) from the selected Site(SPWeb): {1}"
                + "\n Continue ?",
                clbSPWebFeatures.CheckedItems.Count,
                m_CurrentWebLocation.Url
                );
            if (MessageBox.Show(msgString, "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            using (WaitCursor wait = new WaitCursor())
            {
                List<ActivatedFeature> webFeatures = 
                    GridConverter.SelectedRowsToActivatedFeature(clbSPWebFeatures.CheckedItems);

                repository.DeactivateFeatures(webFeatures, repository.AlwaysUseForce);
            }

            msgString = "Done. Please Reload, when all features are removed!";
            Log.Information(msgString);
        }

        /// <summary>Removes selected features from the current site collection only</summary>
        private void removeSPSiteFeaturesFromCurrentSite()
        {
            if (clbSPSiteFeatures.CheckedItems.Count == 0)
            {
                MessageBox.Show(Constants.Text.NOFEATURESELECTED);
                Log.Warning(Constants.Text.NOFEATURESELECTED);
                return;
            }
            if (clbSPWebFeatures.CheckedItems.Count > 0) { throw new Exception("Mixed mode unsupported"); }
            if (IsEmpty(m_CurrentSiteLocation))
            {
                MessageBox.Show("No site collection currently selected");
                return;
            }

            string msgString;
            msgString = "This will remove/deactivate the selected Feature(s) from the selected Site Collection only. Continue ?";
            if (MessageBox.Show(msgString, "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }


            using (WaitCursor wait = new WaitCursor())
            {
                List<ActivatedFeature> siteFeatures =
                    GridConverter.SelectedRowsToActivatedFeature(clbSPSiteFeatures.CheckedItems);

                repository.DeactivateFeatures(siteFeatures, repository.AlwaysUseForce);
            }


            msgString = "Done. Please refresh the feature list, when all features are removed!";
            Log.Information(msgString);
        }

       

        /// <summary>Removes selected features from the current SiteCollection only</summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnRemoveFromSiteCollection_Click(object sender, EventArgs e)
        {
            if ((clbSPSiteFeatures.CheckedItems.Count == 0) && (clbSPWebFeatures.CheckedItems.Count == 0))
            {
                MessageBox.Show(Constants.Text.NOFEATURESELECTED);
                Log.Warning(Constants.Text.NOFEATURESELECTED);
            }

            string msgString = string.Empty;

            if (clbSPWebFeatures.CheckedItems.Count == 0)
            {
                // Only site collection features
                // normal removal of SiteColl Features from one site collection
                removeSPSiteFeaturesFromCurrentSite();
                return;
            }

            int featuresRemoved = 0;
            if (clbSPSiteFeatures.CheckedItems.Count != 0)
            {
                string msg = "Cannot remove site features and web features simultaneously";
                MessageBox.Show(msg);
                return;
            }

            // only remove SPWeb features from a site collection
            msgString = "This will force remove/deactivate the selected Site (SPWeb) scoped Feature(s) from all sites within the selected SiteCollections. Continue ?";
            if (MessageBox.Show(msgString, "Warning - Multi Site Deletion!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            using (WaitCursor wait = new WaitCursor())
            {
                var features = GridConverter.SelectedRowsToActivatedFeature(clbSPWebFeatures.CheckedItems);

                featuresRemoved = repository.DeactivateFeatures(features, repository.AlwaysUseForce);
            }
            removeReady(featuresRemoved);
        }
        /// <summary>Removes selected features from the current Web Application only</summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnRemoveFromWebApp_Click(object sender, EventArgs e)
        {
            int featuresSelected = clbSPSiteFeatures.CheckedItems.Count + clbSPWebFeatures.CheckedItems.Count;
            if (featuresSelected == 0)
            {
                MessageBox.Show(Constants.Text.NOFEATURESELECTED);
                Log.Warning(Constants.Text.NOFEATURESELECTED);
                return;
            }

            int featuresRemoved = 0;

            string title = m_CurrentWebAppLocation.DisplayName;
            string msgString = string.Empty;
            msgString = "The " + featuresSelected + " selected Feature(s) " +
                "will be removed/deactivated from the complete web application: " + title + ". Continue?";
            if (MessageBox.Show(msgString, "Warning - Multi Site Deletion!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            using (WaitCursor wait = new WaitCursor())
            {
                var parent = m_CurrentWebAppLocation.Id;
                List<ActivatedFeature> scFeatures = GridConverter.SelectedRowsToActivatedFeature( clbSPSiteFeatures.CheckedItems);
                featuresRemoved = repository.DeactivateFeatures(parent, scFeatures, repository.AlwaysUseForce);

                List<ActivatedFeature> webFeatures = GridConverter.SelectedRowsToActivatedFeature(clbSPWebFeatures.CheckedItems);
                

            }
            removeReady(featuresRemoved);
        }

        
        
        /// <summary>Removes selected features from the whole Farm</summary>
        private void btnRemoveFromFarm_Click(object sender, EventArgs e)
        {
            int featuresSelected = clbSPSiteFeatures.CheckedItems.Count + clbSPWebFeatures.CheckedItems.Count;
            if (featuresSelected == 0)
            {
                MessageBox.Show(Constants.Text.NOFEATURESELECTED);
                Log.Warning(Constants.Text.NOFEATURESELECTED);
                return;
            }

            int featuresRemoved = 0;

            string msgString = string.Empty;
            msgString = "The " + featuresSelected + " selected Feature(s) " +
                "will be removed/deactivated in the complete Farm! Continue?";
            if (MessageBox.Show(msgString, "Warning - Multi Site Deletion!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
            using (WaitCursor wait = new WaitCursor())
            {
                List<ActivatedFeature> scFeatures = GridConverter.SelectedRowsToActivatedFeature(clbSPSiteFeatures.CheckedItems);
                repository.DeactivateFeaturesRecursive(repository.GetFeatureParentFarm(), scFeatures, repository.AlwaysUseForce);
                List<ActivatedFeature> webFeatures = GridConverter.SelectedRowsToActivatedFeature(clbSPSiteFeatures.CheckedItems);
                repository.DeactivateFeaturesRecursive(repository.GetFeatureParentFarm(), webFeatures, repository.AlwaysUseForce);


            }
            removeReady(featuresRemoved);
        }

        #endregion

        #region Feature Activation + Deactivation

        private void PromptAndActivateSelectedFeaturesAcrossSpecifiedScope(SPFeatureScope activationScope, FeatureParent currentLocation, FeatureActivator.Action action)
        {
            List<FeatureDefinition> selectedFeatures = GetSelectedFeatureDefinitions();

            PromptAndActivateSelectedFeaturesAcrossSpecifiedScope(
                selectedFeatures,
            activationScope,
                 currentLocation,
                action
                );

        }
        public void PromptAndActivateSelectedFeaturesAcrossSpecifiedScope(
            List<FeatureDefinition> selectedFeatures,
            SPFeatureScope activationScope,
            FeatureParent currentLocation)
        {

            if (selectedFeatures.Count > 10)
            {
                InfoBox(string.Format(
                    "Too many features ({0}) selected; max is 10 at a time",
                    selectedFeatures.Count
                    ));
                return;
            }

 
            var msg = string.Format(
                "Processing the selected {0} features: \n"
                + "{1}"
                + "in the selected {2}: \n\t{3}",
                selectedFeatures.Count,
                GetFeatureSummaries(selectedFeatures, "\t{Name} ({Scope})\n"),
                activationScope,
                currentLocName
                );
            if (!ConfirmBoxOkCancel(msg))
            {
                return;
            }
            FeatureActivator.Forcefulness forcefulness = FeatureActivator.Forcefulness.Regular;
            msg = string.Format("Use Force flag?");
            if (ConfirmBoxYesNo(msg))
            {
                forcefulness = FeatureActivator.Forcefulness.Forcible;
            }
            FeatureActivator activator = new FeatureActivator(repository, action, featureSet);
            activator.ExceptionLoggingListeners += new FeatureActivator.ExceptionLoggerHandler(activator_ExceptionLoggingListeners);
            activator.InfoLoggingListeners += activator_InfoLoggingListeners;

            switch (activationScope)
            {
                case SPFeatureScope.Farm:
                    {
                        activator.TraverseActivateFeaturesInFarm(forcefulness);
                    }
                    break;

                case SPFeatureScope.WebApplication:
                    {
                        SPWebApplication webapp = GetCurrentWebApplication();
                        activator.TraverseActivateFeaturesInWebApplication(webapp, forcefulness);
                    }
                    break;

                case SPFeatureScope.Site:
                    {
                        using (SPSite site = OpenCurrentSite())
                        {
                            if (site == null) { return; }
                            try
                            {
                                activator.TraverseActivateFeaturesInSiteCollection(site, forcefulness);
                            }
                            finally
                            {
                                site.Dispose();
                            }
                        }
                    }
                    break;

                case SPFeatureScope.Web:
                    {
                        using (SPSite site = OpenCurrentSite())
                        {
                            if (site == null) { return; }
                            try
                            {
                                using (SPWeb web = site.OpenWeb())
                                {
                                    if (web == null) { return; }
                                    try
                                    {
                                        activator.ActivateFeaturesInWeb(web, forcefulness);
                                    }
                                    finally
                                    {
                                        site.Dispose();
                                    }
                                }
                            }
                            finally
                            {
                                site.Dispose();
                            }
                        }
                    }
                    break;
                default:
                    {
                        msg = "Unknown scope: " + activationScope.ToString();
                        ErrorBox(msg);
                    }
                    break;
            }
            msg = string.Format(
                "{0} Feature(s) {1}.",
                activator.Activations,
                verbpast);
            MessageBox.Show(msg);
            Log.Information(msg);
        }

        private static string GetFeatureSummaries(List<Feature> features, string format)
        {
            StringBuilder featureNames = new StringBuilder();
            foreach (Feature feature in features)
            {
                string text = format
                    .Replace("{Name}", feature.Name)
                    .Replace("{Scope}", feature.Scope.ToString())
                    .Replace("{Id}", feature.Id.ToString())
                    ;
                featureNames.Append(text);
            }
            return featureNames.ToString();
        }
        #endregion

        #region Helper Methods

        private void logFeatureSelected()
        {
            if (clbSPSiteFeatures.CheckedItems.Count + clbSPWebFeatures.CheckedItems.Count > 0)
            {

                // enable all remove buttons
                removeBtnEnabled(true);

                Log.Information("Feature selection changed:");
                if (clbSPSiteFeatures.CheckedItems.Count > 0)
                {
                    foreach (ActivatedFeature checkedFeature in clbSPSiteFeatures.CheckedItems)
                    {
                        Log.Information(checkedFeature.ToString() + ", Scope: Site");
                    }
                }

                if (clbSPWebFeatures.CheckedItems.Count > 0)
                {
                    foreach (ActivatedFeature checkedFeature in clbSPWebFeatures.CheckedItems)
                    {
                        Log.Information(checkedFeature.ToString() + ", Scope: Web");
                    }
                }
            }
            else
            {
                // disable all remove buttons
                removeBtnEnabled(false);
            }
        }

        /// <summary>
        /// Forcefully delete specified features from specified collection
        /// (Could be SPFarm.Features, or SPWebApplication.Features, or etc.)
        /// </summary>
        private int ForceRemoveFeaturesFromLocation(FeatureParent location, SPFeatureCollection spfeatureSet, List<Feature> featuresToRemove)
        {
            int removedFeatures = 0;
            foreach (Feature feature in featuresToRemove)
            {
                ForceRemoveFeatureFromLocation(location, spfeatureSet, feature.Id);
                removedFeatures++;
            }
            return removedFeatures;
        }

        /// <summary>forcefully removes a feature from a featurecollection</summary>
        /// <param name="id">Feature ID</param>
        public void ForceRemoveFeatureFromLocation(FeatureParent location, SPFeatureCollection spfeatureSet, Guid featureId)
        {
            try
            {
                spfeatureSet.Remove(featureId, true);
            }
            catch (Exception exc)
            {
                Log.Error(string.Format(
                    "Trying to remove feature {0} from {1}",
                    featureId, LocationManager.SafeDescribeLocation(location)),
                    exc
                    );
            }
        }

        /// <summary>remove all Web scoped features from a SiteCollection</summary>
        /// <param name="site"></param>
        /// <param name="featureID"></param>
        /// <returns>number of deleted features</returns>
        private int removeWebFeaturesWithinSiteCollection(SPSite site, Guid featureID)
        {
            int removedFeatures = 0;
            int scannedThrough = 0;
            SPSecurity.RunWithElevatedPrivileges(delegate ()
            {
                foreach (SPWeb web in site.AllWebs)
                {
                    try
                    {
                        //forcefully remove the feature
                        if (web.Features[featureID].DefinitionId != null)
                        {
                            bool force = true;
                            web.Features.Remove(featureID, force);
                            removedFeatures++;
                            Log.Information(
                                string.Format("Success removing feature {0} from {1}",
                                featureID,
                                LocationManager.SafeDescribeObject(web)));

                        }
                    }
                    catch (Exception exc)
                    {
                        Log.Error(
                            string.Format("Exception removing feature {0} from {1}",
                            featureID,
                            LocationManager.SafeDescribeObject(web)),
                            exc
                            );
                    }
                    finally
                    {
                        scannedThrough++;
                        if (web != null)
                        {
                            web.Dispose();
                        }
                    }
                }
                string msgString = removedFeatures + " Web Scoped Features removed in the SiteCollection " + site.Url.ToString() + ". " + scannedThrough + " sites/subsites were scanned.";
                Log.Information("  SiteColl - " + msgString);
            });
            return removedFeatures;
        }

        /// <summary>remove all features within a web application, if feature is web scoped, different method is called</summary>
        /// <param name="webApp"></param>
        /// <param name="featureID"></param>
        /// <param name="trueForSPWeb"></param>
        /// <returns>number of deleted features</returns>
        private int removeFeaturesWithinWebApp(SPWebApplication webApp, Guid featureID, SPFeatureScope featureScope)
        {
            int removedFeatures = 0;
            int scannedThrough = 0;
            string msgString;

            msgString = "Removing Feature '" + featureID.ToString() + "' from Web Application: '" + webApp.Name.ToString() + "'.";
            Log.Information(" WebApp - " + msgString);

            SPSecurity.RunWithElevatedPrivileges(delegate ()
                {
                    if (featureScope == SPFeatureScope.WebApplication)
                    {
                        try
                        {
                            webApp.Features.Remove(featureID, true);
                            removedFeatures++;
                            Log.Information(
                                string.Format("Success removing feature {0} from {1}",
                                featureID,
                                LocationManager.SafeDescribeObject(webApp)));
                        }
                        catch (Exception exc)
                        {
                            Log.Error(
                                string.Format("Exception removing feature {0} from {1}",
                                featureID,
                                LocationManager.SafeDescribeObject(webApp)),
                                exc
                                );
                        }
                    }
                    else
                    {

                        foreach (SPSite site in webApp.Sites)
                        {
                            using (site)
                            {
                                if (featureScope == SPFeatureScope.Web)
                                {
                                    removedFeatures += removeWebFeaturesWithinSiteCollection(site, featureID);
                                }
                                else
                                {
                                    try
                                    {
                                        //forcefully remove the feature
                                        site.Features.Remove(featureID, true);
                                        removedFeatures += 1;
                                        Log.Information(
                                            string.Format("Success removing feature {0} from {1}",
                                            featureID,
                                            LocationManager.SafeDescribeObject(site)));
                                    }
                                    catch (Exception exc)
                                    {
                                        Log.Error(
                                            string.Format("Exception removing feature {0} from {1}",
                                            featureID,
                                            LocationManager.SafeDescribeObject(site)),
                                            exc);
                                    }
                                }
                                scannedThrough++;
                            }
                        }
                    }

                });
            msgString = removedFeatures + " Features removed in the Web Application. " + scannedThrough + " SiteCollections were scanned.";
            Log.Information(" WebApp - " + msgString);

            return removedFeatures;
        }

        /// <summary>removes the defined feature within a complete farm</summary>
        /// <param name="featureID"></param>
        /// <param name="trueForSPWeb"></param>
        /// <returns>number of deleted features</returns>
        public int RemoveFeaturesWithinFarm(Guid featureID, SPFeatureScope featureScope)
        {
            int removedFeatures = 0;
            int scannedThrough = 0;
            string msgString;

            SPSecurity.RunWithElevatedPrivileges(delegate ()
            {
                msgString = "Removing Feature '" + featureID.ToString() + ", Scope: " + featureScope.ToString() + "' from the Farm.";
                Log.Information("Farm - " + msgString);
                if (featureScope == SPFeatureScope.Farm)
                {
                    try
                    {
                        SPWebService.ContentService.Features.Remove(featureID, true);
                        removedFeatures++;
                        Log.Information(
                            string.Format("Success removing feature {0} from {1}",
                            featureID,
                            LocationManager.SafeDescribeObject(SPFarm.Local)));
                    }
                    catch (Exception exc)
                    {
                        Log.Error(
                            string.Format("Exception removing feature {0} from farm",
                            featureID,
                            LocationManager.SafeDescribeObject(SPFarm.Local)),
                            exc
                            );

                        Log.Warning("Farm - The Farm Scoped feature '" + featureID.ToString() + "' was not found. ");
                    }
                }
                else
                {

                    // all the content & admin WebApplications 
                    List<SPWebApplication> webApplicationCollection = GetAllWebApps();

                    foreach (SPWebApplication webApplication in webApplicationCollection)
                    {

                        removedFeatures += removeFeaturesWithinWebApp(webApplication, featureID, featureScope);
                        scannedThrough++;
                    }
                }
                msgString = removedFeatures + " Features removed in the Farm. " + scannedThrough + " Web Applications were scanned.";
                Log.Information("Farm - " + msgString);
            });
            return removedFeatures;
        }


        /// <summary>Uninstall a collection of Farm Feature Definitions</summary>
        private void UninstallSelectedFeatureDefinitions(List<FeatureDefinition> features, bool force)
        {
            Exception exception = null;

            foreach (FeatureDefinition feature in features)
            {
                string excMsg = string.Format(
                       "Exception uninstalling feature defintion {0}",
                       feature.Id);

                try
                {
                    FeatureDefinitionUninstall.Uninstall(
                        feature.Id,
                        feature.CompatibilityLevel,
                        force, out exception);

                    if (exception != null)
                    {
                        Log.Error("Inner " + excMsg, exception);
                    }
                }
                catch (Exception exc)
                {
                   
                    Log.Error(excMsg, exc);
                }
            }
        }

        /// <summary>enables or disables all buttons for feature removal</summary>
        /// <param name="enabled">true = enabled, false = disabled</param>
        private void removeBtnEnabled(bool enabled)
        {
            btnRemoveFromWeb.Enabled = enabled;
            btnRemoveFromSiteCollection.Enabled = enabled;
            btnRemoveFromWebApp.Enabled = enabled;
            btnRemoveFromFarm.Enabled = enabled;
        }

        /// <summary>enables or disables all buttons for feature definition administration</summary>
        /// <param name="enabled">true = enabled, false = disabled</param>
        private void EnableActionButtonsAsAppropriate()
        {
            bool bDb = repository.IsLoaded();

            SPFeatureScope lowestScope = GetLowestSelectedScope();

            bool bWeb = !IsEmpty(m_CurrentWebLocation) && lowestScope >= SPFeatureScope.Web;
            bool bSite = !IsEmpty(m_CurrentSiteLocation) && lowestScope >= SPFeatureScope.Site;
            bool bWebApp = !IsEmpty(m_CurrentWebAppLocation) && lowestScope >= SPFeatureScope.WebApplication;

            btnUninstFDef.Enabled = bDb && (gridFeatureDefinitions.SelectedRows.Count <= 10);

            btnActivateSPWeb.Enabled = bDb && bWeb;
            btnDeactivateSPWeb.Enabled = bDb && bWeb;
            btnActivateSPSite.Enabled = bDb && bSite;
            btnDeactivateSPSite.Enabled = bDb && bSite;
            btnActivateSPWebApp.Enabled = bDb && bWebApp;
            btnDeactivateSPWebApp.Enabled = bDb && bWebApp;
            btnActivateSPFarm.Enabled = bDb;
        }

        private SPFeatureScope GetLowestSelectedScope()
        {
            SPFeatureScope minScope = SPFeatureScope.ScopeInvalid;
            foreach (DataGridViewRow row in gridFeatureDefinitions.SelectedRows)
            {
                Feature feature = row.DataBoundItem as Feature;
                if (minScope == SPFeatureScope.ScopeInvalid || feature.Scope < minScope)
                {
                    minScope = feature.Scope;
                }
            }
            return minScope;
        }

        private void removeReady(int featuresRemoved)
        {
            string msgString;
            msgString = featuresRemoved.ToString() + " Features were removed. Please 'Reload all Data'!";
            MessageBox.Show(msgString);
            Log.Information(msgString);
        }

        private string GetFeatureSolutionInfo(SPFeature feature)
        {
            string text = "";
            try
            {
                if (feature.Definition != null
                    && feature.Definition.SolutionId != Guid.Empty)
                {
                    text = string.Format("SolutionId={0}", feature.Definition.SolutionId);
                    SPSolution solution = SPFarm.Local.Solutions[feature.Definition.SolutionId];
                    if (solution != null)
                    {
                        try
                        {
                            text += string.Format(", SolutionName='{0}'", solution.Name);
                        }
                        catch { }
                        try
                        {
                            text += string.Format(", SolutionDisplayName='{0}'", solution.DisplayName);
                        }
                        catch { }
                        try
                        {
                            text += string.Format(", SolutionDeploymentState='{0}'", solution.DeploymentState);
                        }
                        catch { }
                    }
                }
            }
            catch
            {
            }
            return text;
        }

        #endregion

        #region Feature lists, WebApp, SiteCollection and SPWeb list set up

        /// <summary>trigger reload of in memory database</summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnRefreshAllContent_Click(object sender, EventArgs e)
        {
            using (WaitCursor wait = new WaitCursor())
            {
                
                EnableActionButtonsAsAppropriate();
                LoadWebAppGrid();
            
                this.gridFeatureDefinitions.DataSource = null;

                ReloadAllActivationData(); // reloads defintions & activation data

                var featureDefinitions = repository.GetFeatureDefinitions();

                this.gridFeatureDefinitions.DataSource = new SortableBindingList<FeatureDefinition>(featureDefinitions);

                Log.Information("Feature Definition list updated.");
            }
        }

        private void ConfigureWebApplicationsGrid()
        {
            bool relativeUrls = false;
            ConfigureLocationGrid(gridWebApplications, relativeUrls);
        }
        private void ConfigureSiteCollectionsGrid()
        {
            bool relativeUrls = true;
            ConfigureLocationGrid(gridSiteCollections, relativeUrls);
        }
        private void ConfigureWebsGrid()
        {
            bool relativeUrls = true;
            ConfigureLocationGrid(gridWebs, relativeUrls);
        }
        private void ConfigureLocationGrid(DataGridView grid, bool relativeUrls)
        {
            grid.AutoGenerateColumns = false;
            string urlField = (relativeUrls ? "RelativeUrl" : "FullUrl");
            GridColMgr.AddTextColumn(grid, urlField, 100);
            GridColMgr.AddTextColumn(grid, "Name", 150);
            GridColMgr.AddTextColumn(grid, "Id", 100);

            // Set all columns sortable
            foreach (DataGridViewColumn column in grid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.Automatic;
            }
            // TODO - analogue to CreateFeatureDefContextMenu ?
        }

        private FeatureParent GetSelectedWebApp()
        {
            if (gridWebApplications.SelectedRows.Count != 1) { return null; }
            DataGridViewRow row = gridWebApplications.SelectedRows[0];
            FeatureParent location = (row.DataBoundItem as FeatureParent);
            return location;
        }

        /// <summary>populate the web application list</summary>
        private void LoadWebAppGrid()
        {
            using (WaitCursor wait = new WaitCursor())
            {
                gridWebApplications.DataSource = null;
                ClearCurrentSiteCollectionData();
                ClearCurrentWebData();
                removeBtnEnabled(false);

                SortableBindingList<FeatureParent> webapps = new SortableBindingList<FeatureParent>(
                    repository.GetSharePointWebApplications()
                    );
                gridWebApplications.DataSource = webapps;
                if (webapps.Count > 0)
                {
                    gridSiteCollections.Enabled = true;
                    // If there is any row, select the first
                    if (gridWebApplications.Rows.Count >0)
                    {
                        gridWebApplications.Rows[0].Selected = true;
                    }
                }
                else
                {
                    gridSiteCollections.Enabled = false;
                }
            }
        }

        /// <summary>Update SiteCollections list when a user changes the selection in Web Application list
        /// </summary>
        private void gridWebApplications_SelectionChanged(object sender, EventArgs e)
        {
            ReloadCurrentSiteCollections();
            EnableActionButtonsAsAppropriate();
        }

        private void ReloadCurrentSiteCollections()
        {
            using (WaitCursor wait = new WaitCursor())
            {
                try
                {
                    ClearCurrentSiteCollectionData();
                    ClearCurrentWebData();
                    removeBtnEnabled(false);

                    m_CurrentWebAppLocation = GetSelectedWebApp();
                    if (m_CurrentWebAppLocation == null) { return; }
                    SPWebApplication webApp = GetCurrentWebApplication();
                    SortableBindingList<FeatureParent> sitecolls = new SortableBindingList<FeatureParent>();
                    foreach (SPSite site in webApp.Sites)
                    {
                        try
                        {
                            FeatureParent siteLocation = LocationManager.GetLocation(site);
                            sitecolls.Add(siteLocation);
                        }
                        catch (Exception exc)
                        {
                            Log.Error(
                                string.Format("Exception enumerating site: {0}",
                                LocationManager.SafeDescribeObject(site)),
                                exc);
                        }
                    }
                    gridSiteCollections.DataSource = sitecolls;
                    // select first site collection if there is only one
                    if (gridSiteCollections.RowCount == 1)
                    {
                        gridSiteCollections.Rows[0].Selected = true;
                    }
                }
                catch (Exception exc)
                {
                    Log.Error("Exception enumerating site collections", exc);
                }
            }
        }

        /// <summary>UI method to update the SPWeb list when a user changes the selection in site collection list
        /// </summary>
        private void gridSiteCollections_SelectionChanged(object sender, EventArgs e)
        {
            ReloadCurrentWebs();
            EnableActionButtonsAsAppropriate();
        }

        private FeatureParent GetSelectedSiteCollection()
        {
            if (gridSiteCollections.SelectedRows.Count != 1) { return null; }
            DataGridViewRow row = gridSiteCollections.SelectedRows[0];
            FeatureParent location = (row.DataBoundItem as FeatureParent);
            return location;
        }

        private void ReloadCurrentWebs()
        {
            using (WaitCursor wait = new WaitCursor())
            {
                try
                {
                    m_CurrentSiteLocation = GetSelectedSiteCollection();
                    ClearCurrentWebData();
                    ReloadCurrentSiteCollectionFeatures();
                    ReloadSubWebList();
                }
                catch (Exception exc)
                {
                    Log.Error("Exception enumerating webs", exc);
                }
            }
        }

        private void ClearCurrentSiteCollectionData()
        {
            Models.FeatureParent.Clear(m_CurrentSiteLocation);
            gridSiteCollections.DataSource = null;
            clbSPSiteFeatures.Items.Clear();
        }

        private void ClearCurrentWebData()
        {
            Models.FeatureParent.Clear(m_CurrentWebLocation);
            gridWebs.DataSource = null;
            clbSPWebFeatures.Items.Clear();
        }

        private void ReloadCurrentSiteCollectionFeatures()
        {
            clbSPSiteFeatures.Items.Clear();
            if (IsEmpty(m_CurrentSiteLocation))
            { return; }
            List<Feature> features = repository.GetFeaturesOfLocation(m_CurrentSiteLocation);
            features.Sort();
            clbSPSiteFeatures.Items.AddRange(features.ToArray());
        }

        private void ReloadCurrentWebFeatures()
        {
            clbSPWebFeatures.Items.Clear();
            if (IsEmpty(m_CurrentWebLocation))
            { return; }
            List<Feature> features = repository.GetFeaturesOfLocation(m_CurrentWebLocation);
            features.Sort();
            clbSPWebFeatures.Items.AddRange(features.ToArray());
        }

        private static bool IsEmpty(FeatureParent location)
        {
            return Models.FeatureParent.IsLocationEmpty(location);
        }

        private FeatureParent GetSelectedWeb()
        {
            if (gridWebs.SelectedRows.Count != 1) { return null; }
            DataGridViewRow row = gridWebs.SelectedRows[0];
            FeatureParent location = (row.DataBoundItem as FeatureParent);
            return location;
        }

        private void ReloadSubWebList()
        {
            try
            {
                removeBtnEnabled(false);

                m_CurrentSiteLocation = GetSelectedSiteCollection();
                if (m_CurrentSiteLocation == null) {
                    
                    return; }

                var webs = repository.GetSharePointChildHierarchy(m_CurrentSiteLocation.Id);
                if (webs == null || webs.Count == 0)
                {
                    Log.Warning("No webs found for site " +
                        m_CurrentSiteLocation.Url);
                    return;
                }
                
                gridWebs.DataSource = new SortableBindingList<FeatureParent>(webs);
                
                // select first web if there is only one
                if (gridWebs.RowCount == 1)
                {
                    gridWebs.Rows[0].Selected = true;
                }
            }
            catch (Exception exc)
            {
                Log.Error("Exception enumerating subwebs", exc);
            }
        }

        /// <summary>UI method to load the Web Features and Site Features
        /// Handles the SelectedIndexChanged event of the listWebs control.
        /// </summary>
        private void gridWebs_SelectionChanged(object sender, EventArgs e)
        {
            using (WaitCursor wait = new WaitCursor())
            {
                m_CurrentWebLocation = GetSelectedWeb();
                ReloadCurrentWebFeatures();
            }
            EnableActionButtonsAsAppropriate();
        }

        private void clbSPSiteFeatures_SelectedIndexChanged(object sender, EventArgs e)
        {
            logFeatureSelected();
        }

        private void clbSPWebFeatures_SelectedIndexChanged(object sender, EventArgs e)
        {
            logFeatureSelected();
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            logTextWriter.ClearLog();
        }

        private void gridFeatureDefinitions_SelectionChanged(object sender, EventArgs e)
        {
            EnableActionButtonsAsAppropriate();
        }

        private void btnActivateSPWeb_Click(object sender, EventArgs e)
        {
            if (IsEmpty(m_CurrentWebLocation))
            {
                InfoBox("No site (SPWeb) selected");
                return;
            }

            var fds = Common.GridConverter.SelectedRowsToFeatureDefinition(gridFeatureDefinitions.SelectedRows);

                repository.ActivateFeaturesRecursive(m_CurrentWebLocation, fds, repository.AlwaysUseForce);
        }

        private void btnDeactivateSPWeb_Click(object sender, EventArgs e)
        {
            if (IsEmpty(m_CurrentWebLocation))
            {
                InfoBox("No site (SPWeb) selected");
                return;
            }
            var fds = Common.GridConverter.SelectedRowsToFeatureDefinition(gridFeatureDefinitions.SelectedRows);

            repository.DeactivateFeaturesRecursive(m_CurrentWebLocation, fds, repository.AlwaysUseForce);
        }

        private void btnActivateSPSite_Click(object sender, EventArgs e)
        {
            if (IsEmpty(m_CurrentSiteLocation))
            {
                InfoBox("No site collection selected");
                return;
            }

            var fds = Common.GridConverter.SelectedRowsToFeatureDefinition(gridFeatureDefinitions.SelectedRows);

            repository.ActivateFeaturesRecursive(m_CurrentSiteLocation, fds, repository.AlwaysUseForce);
        }

        private void btnDeactivateSPSite_Click(object sender, EventArgs e)
        {
            if (IsEmpty(m_CurrentSiteLocation))
            {
                InfoBox("No site collection selected");
                return;
            }

            var fds = Common.GridConverter.SelectedRowsToFeatureDefinition(gridFeatureDefinitions.SelectedRows);

            repository.DeactivateFeaturesRecursive(m_CurrentSiteLocation, fds, repository.AlwaysUseForce);
        }

        private void btnActivateSPWebApp_Click(object sender, EventArgs e)
        {
            if (IsEmpty(m_CurrentWebAppLocation))
            {
                InfoBox("No web application selected");
                return;
            }
            var fds = Common.GridConverter.SelectedRowsToFeatureDefinition(gridFeatureDefinitions.SelectedRows);

            repository.ActivateFeaturesRecursive(m_CurrentWebAppLocation, fds, repository.AlwaysUseForce);
        }

        private void btnDeactivateSPWebApp_Click(object sender, EventArgs e)
        {
            if (IsEmpty(m_CurrentWebAppLocation))
            {
                InfoBox("No web application selected");
                return;
            }
            var fds = Common.GridConverter.SelectedRowsToFeatureDefinition(gridFeatureDefinitions.SelectedRows);

            repository.DeactivateFeaturesRecursive(m_CurrentWebAppLocation, fds, repository.AlwaysUseForce);
        }

        private void btnActivateSPFarm_Click(object sender, EventArgs e)
        {
            var fds = Common.GridConverter.SelectedRowsToFeatureDefinition(gridFeatureDefinitions.SelectedRows);

            repository.ActivateFeaturesRecursive(repository.GetFeatureParentFarm(), fds, repository.AlwaysUseForce);
        }

        private void btnDeactivateSPFarm_Click(object sender, EventArgs e)
        {
            var fds = Common.GridConverter.SelectedRowsToFeatureDefinition(gridFeatureDefinitions.SelectedRows);

            repository.DeactivateFeaturesRecursive(repository.GetFeatureParentFarm(), fds, repository.AlwaysUseForce);
        }

        private List<FeatureParent> GetFeatureLocations(Guid featureId)
        {
            if (!repository.IsLoaded())
            {
                ReloadAllActivationData();
            }
            return repository.GetLocationsOfFeature(featureId);
        }

        private void ReloadAllActivationData()
        {
            using (WaitCursor wait = new WaitCursor())
            {
                ActivationFinder finder = new ActivationFinder();

                // Call routine to actually find & report activations
                repository.LoadAllData(finder.FindAllActivationsOfAllFeatures());
                repository.MarkFaulty(finder.GetFaultyFeatureIdList());
                lblFeatureDefinitions.Text = string.Format(
                    "All {0} Features installed in the Farm",
                    repository.GetAllFeaturesCount());
            }
        }

        private List<SPWebApplication> GetAllWebApps()
        {
            List<SPWebApplication> webapps = new List<SPWebApplication>();
            foreach (SPWebApplication contentApp in SPWebService.ContentService.WebApplications)
            {
                webapps.Add(contentApp);
            }
            foreach (SPWebApplication adminApp in SPWebService.AdministrationService.WebApplications)
            {
                webapps.Add(adminApp);
            }
            return webapps;
        }

        private void FrmMain_Load(object sender, EventArgs e)
        {

        }

        #endregion
        #region MessageBoxes
        private static void ErrorBox(string text)
        {
            ErrorBox(text, "Error");
        }
        private static void ErrorBox(string text, string caption)
        {
            MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        public static bool ConfirmBoxYesNo(string text)
        {
            return ConfirmBoxYesNo(text, "Confirm");
        }
        public static bool ConfirmBoxYesNo(string text, string caption)
        {
            DialogResult rtn = MessageBox.Show(
                text, caption,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return (rtn == DialogResult.Yes);
        }
        private static bool ConfirmBoxOkCancel(string text)
        {
            return ConfirmBoxOkCancel(text, "Confirm");
        }
        private static bool ConfirmBoxOkCancel(string text, string caption)
        {
            DialogResult rtn = MessageBox.Show(
                text, caption,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return (rtn == DialogResult.OK);
        }
        private static void InfoBox(string text)
        {
            InfoBox(text, "");
        }
        private static void InfoBox(string text, string caption)
        {
            MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        #endregion

        private void gridFeatureDefinitions_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            DataGridView grid = sender as DataGridView;
            if (grid.Columns[e.ColumnIndex].DataPropertyName == "Activations")
            {
                FeatureDefinition feature = grid.Rows[e.RowIndex].DataBoundItem as FeatureDefinition;
                FeatureLocationSet set = new FeatureLocationSet();
                ReviewActivationsOfFeature(feature);
            }
        }

        

        private void gridFeatureDefinitions_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            m_featureDefGridContextFeature = null;
            if (e.RowIndex < 0 || e.ColumnIndex < 0) { return; } // Ignore if out of data area
            if (e.Button == MouseButtons.Right)
            {
                // Find feature def on which user right-clicked
                DataGridView grid = sender as DataGridView;
                DataGridViewRow row = grid.Rows[e.RowIndex];
                m_featureDefGridContextFeature = row.DataBoundItem as Feature;
                if (GetIntValue(m_featureDefGridContextFeature.Activations, 0) == 0)
                {
                    // no context menu if feature has no activations
                    // because the context menu gets stuck up if the no activations
                    // message box appears
                    return;
                }
                if (m_featureDefGridContextFeature.Id == Guid.Empty)
                {
                    // no context menu if no valid id
                    return;
                }
                gridFeatureDefinitions.ContextMenuStrip = m_featureDefGridContextMenu;
                UpdateGridContextMenu();
            }
        }

        private void UpdateGridContextMenu()
        {
            Feature feature = m_featureDefGridContextFeature;
            ContextMenuStrip ctxtmenu = m_featureDefGridContextMenu;
            ToolStripItem header = ctxtmenu.Items.Find("Header", true)[0];
            header.Text = string.Format("Feature: {0}", GetFeatureNameOrId(feature));
            ToolStripItem activations = ctxtmenu.Items.Find("Activations", true)[0];
            activations.Text = string.Format("View {0} activations", GetIntValue(feature.Activations, 0));
        }

        private static string GetFeatureNameOrId(Feature feature)
        {
            if (!string.IsNullOrEmpty(feature.Name))
            {
                return feature.Name;
            }
            else
            {
                return feature.Id.ToString();
            }
        }

        private void gridFeatureDefinitions_ViewActivationsClick(object sender, EventArgs e)
        {
            ReviewActivationsOfFeature(m_featureDefGridContextFeature);
        }

        private static int GetIntValue(int? value, int defval)
        {
            return value.HasValue ? value.Value : defval;
        }

        private void btnViewActivations_Click(object sender, EventArgs e)
        {
            if (gridFeatureDefinitions.SelectedRows.Count < 1)
            {
                InfoBox("No features selected to review activations");
                return;
            }
            FeatureLocationSet set = new FeatureLocationSet();
            foreach (Feature feature in GetSelectedFeatureDefinitions())
            {
                set.Add(feature, GetFeatureLocations(feature.Id));
            }
            ReviewActivationsOfFeatures(set);
        }

        private void gridFeatureDefinitions_MouseDown(object sender, MouseEventArgs e)
        {
            gridFeatureDefinitions.ContextMenuStrip = null;
            m_featureDefGridContextFeature = null;
        }

        private void useForce_CheckedChanged(object sender, EventArgs e)
        {
            repository.AlwaysUseForce = useForce.Checked;
        }

        private void ActionWebCaption_Click(object sender, EventArgs e)
        {

        }

    }
}
