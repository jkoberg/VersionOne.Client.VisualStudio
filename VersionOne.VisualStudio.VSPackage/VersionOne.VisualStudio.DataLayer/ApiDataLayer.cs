using System;
using System.Collections.Generic;
using System.Linq;
using VersionOne.SDK.APIClient;
using System.Net;
using VersionOne.VisualStudio.DataLayer.Entities;
using VersionOne.VisualStudio.DataLayer.Logging;
using VersionOne.VisualStudio.DataLayer.Settings;

namespace VersionOne.VisualStudio.DataLayer {
    public class ApiDataLayer : IDataLayerInternal {
        #region Private fields

        private readonly VersionOneConnector connector = new VersionOneConnector();

        private static ApiDataLayer dataLayer;
        private readonly static IList<AttributeInfo> AttributesToQuery = new List<AttributeInfo>();

        private RequiredFieldsValidator requiredFieldsValidator;
        public IDictionary<string, IAssetType> Types { get; private set; }

        public IAssetType ProjectType { get; private set; }
        public IAssetType TaskType  { get; private set; }
        public IAssetType TestType  { get; private set; }
        public IAssetType DefectType  { get; private set; }
        public IAssetType StoryType  { get; private set; }
        
        private IAssetType workitemType;
        private IAssetType primaryWorkitemType;
        private IAssetType effortType;

        private IDictionary<string, PropertyValues> listPropertyValues;

        private readonly IList<string> effortTrackingAttributes = new List<string> {
            "DetailEstimate",
            "ToDo",
            "Done",
            "Effort",
            "Actuals",
        };

        private ILogger logger;

        #endregion

        public Oid MemberOid { get; private set; }

        public ILoggerFactory LoggerFactory { get; set; }

        public ILogger Logger {
            get {
                return logger ?? (logger = LoggerFactory == null ? new BlackholeLogger() : LoggerFactory.GetLogger("DataLayer"));
            }
        }

        private ApiDataLayer() {
            var prefixes = new[] {
                Entity.TaskType, 
                Entity.DefectType, 
                Entity.StoryType, 
                Entity.TestType
            };

            foreach(var prefix in prefixes) {
                AttributesToQuery.Add(new AttributeInfo("CheckQuickClose", prefix, false));
                AttributesToQuery.Add(new AttributeInfo("CheckQuickSignup", prefix, false));
            }

            AttributesToQuery.Add(new AttributeInfo("Schedule.EarliestActiveTimebox", Entity.ProjectType, false));
        }

        public string ApiVersion {
            get { return connector.ApiVersion; }
            set { connector.ApiVersion = value; }
        }

        public string NullProjectToken {
            get { return Oid.Null.Token; }
        }

        public string CurrentProjectId { get; set; }

        public Project CurrentProject {
            get {
                if(CurrentProjectId == null) {
                    CurrentProjectId = "Scope:0";
                }

                return GetProjectById(CurrentProjectId);
            }
            set {
                CurrentProjectId = value.Id;
            }
        }

        public bool ShowAllTasks { get; set; }

        public void CommitChanges(IAssetCache assetCache) {
            if(!connector.IsConnected) {
                Logger.Error("Not connected to VersionOne.");
            }

            try {
                var validationResult = new Dictionary<Asset, List<RequiredFieldsDto>>();
                var internalCache = assetCache.ToInternalCache();

                var workitems = assetCache.GetWorkitems(true);

                foreach(var item in workitems) {
                    if(!ValidateWorkitemAndCommitOnSuccess(item, internalCache.Efforts, validationResult)) {
                        continue;
                    }

                    foreach(var child in item.Children) {
                        ValidateWorkitemAndCommitOnSuccess(child, internalCache.Efforts, validationResult);
                    }
                }

                if(validationResult.Count > 0) {
                    throw new ValidatorException(requiredFieldsValidator.CreateErrorMessage(validationResult));
                }

            } catch(APIException ex) {
                Logger.Error("Failed to commit changes.", ex);
            }
        }

        private bool ValidateWorkitemAndCommitOnSuccess(Workitem item, IDictionary<Asset, double> efforts, IDictionary<Asset, List<RequiredFieldsDto>> validationResults) {
            var itemValidationResult = requiredFieldsValidator.Validate(item.Asset);

            if(itemValidationResult.Count == 0) {
                item.CommitChanges();
                CommitEffort(efforts, item.Asset);
                return true;
            }

            validationResults.Add(item.Asset, itemValidationResult);
            return false;
        }

        private IFilterTerm GetScopeFilter(IAssetType assetType) {
            var terms = new List<FilterTerm>(4);

            var term = new FilterTerm(assetType.GetAttributeDefinition("Scope.AssetState"));
            term.NotEqual(AssetState.Closed);
            terms.Add(term);

            term = new FilterTerm(assetType.GetAttributeDefinition("Scope.ParentMeAndUp"));
            term.Equal(CurrentProjectId);
            terms.Add(term);

            term = new FilterTerm(assetType.GetAttributeDefinition("Timebox.State.Code"));
            term.Equal("ACTV");
            terms.Add(term);

            term = new FilterTerm(assetType.GetAttributeDefinition("AssetState"));
            term.NotEqual(AssetState.Closed);
            terms.Add(term);

            return new AndFilterTerm(terms.ToArray());
        }

        #region Effort tracking

        public EffortTrackingLevel DefectTrackingLevel { get; private set; }
        public EffortTrackingLevel StoryTrackingLevel { get; private set; }
        public bool TrackEffort { get; private set; }

        private static EffortTrackingLevel TranslateEffortTrackingLevel(TrackingLevel level) {
            switch(level) {
                case TrackingLevel.On:
                    return EffortTrackingLevel.PrimaryWorkitem;
                case TrackingLevel.Off:
                    return EffortTrackingLevel.SecondaryWorkitem;
                case TrackingLevel.Mix:
                    return EffortTrackingLevel.Both;
                default:
                    throw new NotSupportedException("Unknown tracking level");
            }
        }

        public bool IsEffortTrackingRelated(string attributeName) {
            return effortTrackingAttributes.Contains(attributeName);
        }

        #endregion

        private void AddSelection(Query query, string typePrefix) {
            foreach (var attrInfo in AttributesToQuery.Where(attrInfo => attrInfo.Prefix == typePrefix)) {
                try {
                    var def = Types[attrInfo.Prefix].GetAttributeDefinition(attrInfo.Attr);
                    query.Selection.Add(def);
                } catch(MetaException ex) {
                    Logger.Warn("Wrong attribute: " + attrInfo, ex);
                }
            }

            if(requiredFieldsValidator.GetFields(typePrefix) == null) {
                return;
            }

            foreach(var field in requiredFieldsValidator.GetFields(typePrefix)) {
                try {
                    var def = Types[typePrefix].GetAttributeDefinition(field.Name);
                    query.Selection.Add(def);
                } catch(MetaException ex) {
                    Logger.Warn("Wrong attribute: " + field.Name, ex);
                }
            }
        }

        private Project GetProjectById(string id) {
            if(!connector.IsConnected) {
                return null;
            }

            if(CurrentProjectId == null) {
                Logger.Error("Current project is not selected");
                throw new DataLayerException("Current project is not selected");
            }

            var query = new Query(Oid.FromToken(id, connector.MetaModel));
            AddSelection(query, Entity.ProjectType);
            
            try {
                var result = connector.Services.Retrieve(query);
                return result.TotalAvaliable == 1 ? WorkitemFactory.CreateProject(result.Assets[0], null) : null;
            } catch(MetaException ex) {
                connector.IsConnected = false;
                throw new DataLayerException("Unable to get projects", ex);
            } catch(Exception ex) {
                throw new DataLayerException("Unable to get projects", ex);
            }
        }

        public void GetWorkitems(IAssetCache assetCache) {
            if(!connector.IsConnected) {
                Logger.Error("Not connected to VersionOne.");
            }
            
            if(CurrentProjectId == null) {
                throw new DataLayerException("Current project is not selected");
            }

            if(assetCache.IsSet) {
                return;
            }
            
            try {
                var parentDef = workitemType.GetAttributeDefinition("Parent");

                var query = new Query(workitemType, parentDef);
                AddSelection(query, Entity.TaskType);
                AddSelection(query, Entity.StoryType);
                AddSelection(query, Entity.DefectType);
                AddSelection(query, Entity.TestType);

                query.Filter = GetScopeFilter(workitemType);

                query.OrderBy.MajorSort(primaryWorkitemType.DefaultOrderBy, OrderBy.Order.Ascending);
                query.OrderBy.MinorSort(workitemType.DefaultOrderBy, OrderBy.Order.Ascending);

                var assetList = connector.Services.Retrieve(query);
                assetCache.ToInternalCache().Set(assetList.Assets);
            } catch(MetaException ex) {
                Logger.Error("Unable to get workitems.", ex);
            } catch(WebException ex) {
                connector.IsConnected = false;
                Logger.Error("Unable to get workitems.", ex);
            } catch(Exception ex) {
                Logger.Error("Unable to get workitems.", ex);
            }
        }

        /// <summary>
        /// Check if asset should be used when Show My Tasks filter is on
        /// </summary>
        /// <param name="asset">Story, Task, Defect or Test</param>
        /// <returns>true if current user is owner of asset, false - otherwise</returns>
        public bool AssetPassesShowMyTasksFilter(Asset asset) {
            if(asset.HasChanged || asset.Oid.IsNull) {
                return true;
            }

            var definition = workitemType.GetAttributeDefinition(Entity.OwnersProperty);
            var attribute = asset.GetAttribute(definition);
            var owners = attribute.Values;
            
            if(owners.Cast<Oid>().Any(oid => oid == MemberOid)) {
                return true;
            }

            if(asset.Children != null) {
                return asset.Children.Any(AssetPassesShowMyTasksFilter);
            }

            return false;
        }

        public IList<Project> GetProjectTree() {
            try {
                var scopeQuery = new Query(ProjectType, ProjectType.GetAttributeDefinition("Parent"));
                var stateTerm = new FilterTerm(ProjectType.GetAttributeDefinition("AssetState"));
                stateTerm.NotEqual(AssetState.Closed);
                scopeQuery.Filter = stateTerm;
                AddSelection(scopeQuery, Entity.ProjectType);
                var result = connector.Services.Retrieve(scopeQuery);
                
                var roots = result.Assets.Select(asset => WorkitemFactory.CreateProject(asset, null)).ToList();
                return roots;
            } catch(WebException ex) {
                connector.IsConnected = false;
                Logger.Error("Can't get projects list.", ex);
                return null;
            } catch(Exception ex) {
                Logger.Error("Can't get projects list.", ex);
                return null;
            }
        }

        public IAssetCache CreateAssetCache() {
            return new AssetCache(this);
        }

        public void CheckConnection(VersionOneSettings settings) {
            try {
                connector.CheckConnection(settings);
            } catch(Exception ex) {
                logger.Error("Cannot connect to V1 server.", ex);
            }
        }

        public bool Connect(VersionOneSettings settings) {
            connector.IsConnected = false;

            try {
                connector.Connect(settings);

                Types = new Dictionary<string, IAssetType>(5);
                ProjectType = GetAssetType(Entity.ProjectType);
                TaskType = GetAssetType(Entity.TaskType);
                TestType = GetAssetType(Entity.TestType);
                DefectType = GetAssetType(Entity.DefectType);
                StoryType = GetAssetType(Entity.StoryType);
                workitemType = connector.MetaModel.GetAssetType("Workitem");
                primaryWorkitemType = connector.MetaModel.GetAssetType("PrimaryWorkitem");

                TrackEffort = connector.V1Configuration.EffortTracking;

                if(TrackEffort) {
                    effortType = connector.MetaModel.GetAssetType("Actual");
                }

                StoryTrackingLevel = TranslateEffortTrackingLevel(connector.V1Configuration.StoryTrackingLevel);
                DefectTrackingLevel = TranslateEffortTrackingLevel(connector.V1Configuration.DefectTrackingLevel);

                MemberOid = connector.Services.LoggedIn;
                listPropertyValues = GetListPropertyValues();
                requiredFieldsValidator = new RequiredFieldsValidator(connector.MetaModel, connector.Services, InternalInstance);
                connector.IsConnected = true;

                return true;
            } catch(MetaException ex) {
                Logger.Error("Cannot connect to V1 server.", ex);
                return false;
            } catch(WebException ex) {
                connector.IsConnected = false;
                Logger.Error("Cannot connect to V1 server.", ex);
                return false;
            } catch(Exception ex) {
                Logger.Error("Cannot connect to V1 server.", ex);
                return false;
            }
        }

        // TODO try to find out why SecurityException might occur here
        private IAssetType GetAssetType(string token) {
            var type = connector.MetaModel.GetAssetType(token);
            Types.Add(token, type);
            return type;
        }

        private static string ResolvePropertyKey(string propertyAlias) {
            switch(propertyAlias) {
                case "DefectStatus":
                    return "StoryStatus";
                case "DefectSource":
                    return "StorySource";
                case "ScopeBuildProjects":
                    return "BuildProject";
                case "TaskOwners":
                case "StoryOwners":
                case "DefectOwners":
                case "TestOwners":
                    return "Member";
            }

            return propertyAlias;
        }

        private Dictionary<string, PropertyValues> GetListPropertyValues() {
            var res = new Dictionary<string, PropertyValues>(AttributesToQuery.Count);
            
            foreach(var attrInfo in AttributesToQuery) {
                if(!attrInfo.IsList) {
                    continue;
                }

                var propertyAlias = attrInfo.Prefix + attrInfo.Attr;

                if(res.ContainsKey(propertyAlias)) {
                    continue;
                }

                var propertyName = ResolvePropertyKey(propertyAlias);

                PropertyValues values;
                
                if(res.ContainsKey(propertyName)) {
                    values = res[propertyName];
                } else {
                    values = QueryPropertyValues(propertyName);
                    res.Add(propertyName, values);
                }

                if(!res.ContainsKey(propertyAlias)) {
                    res.Add(propertyAlias, values);
                }
            }

            return res;
        }

        private PropertyValues QueryPropertyValues(string propertyName) {
            var res = new PropertyValues();
            var assetType = connector.MetaModel.GetAssetType(propertyName);
            var nameDef = assetType.GetAttributeDefinition(Entity.NameProperty);
            IAttributeDefinition inactiveDef;

            var query = new Query(assetType);
            query.Selection.Add(nameDef);
            
            if(assetType.TryGetAttributeDefinition("Inactive", out inactiveDef)) {
                var filter = new FilterTerm(inactiveDef);
                filter.Equal("False");
                query.Filter = filter;
            }

            query.OrderBy.MajorSort(assetType.DefaultOrderBy, OrderBy.Order.Ascending);

            res.Add(new ValueId());
            
            foreach(var asset in connector.Services.Retrieve(query).Assets) {
                var name = asset.GetAttribute(nameDef).Value as string;
                res.Add(new ValueId(asset.Oid, name));
            }

            return res;
        }

        #region Localizer

        public string LocalizerResolve(string key) {
            try {
                return connector.Localizer.Resolve(key);
            } catch(Exception ex) {
                throw new DataLayerException("Failed to resolve key.", ex);
            }
        }

        public bool TryLocalizerResolve(string key, out string result) {
            result = null;

            try {
                if(connector.Localizer != null) {
                    result = connector.Localizer.Resolve(key);
                    return true;
                }
            } catch(V1Exception) {
                Logger.Debug(string.Format("Failed to resolve localized string by key '{0}'", key));
            }

            return false;
        }

        #endregion

        public static IDataLayer Instance {
            get { return dataLayer ?? (dataLayer = new ApiDataLayer()); }
        }

        internal static IDataLayerInternal InternalInstance {
            get { return dataLayer ?? (dataLayer = new ApiDataLayer()); }
        }

        public bool IsConnected {
            get { return connector.IsConnected; }
        }

        /// <exception cref="KeyNotFoundException">If there are no values for this property.</exception>
        public PropertyValues GetListPropertyValues(string propertyName) {
            var propertyKey = ResolvePropertyKey(propertyName);
            return listPropertyValues.ContainsKey(propertyName) ? listPropertyValues[propertyKey] : null;
        }

        public void CommitAsset(IDictionary<Asset, double> efforts, Asset asset) {
            try {
                var requiredData = requiredFieldsValidator.Validate(asset);

                if(requiredData.Count > 0) {
                    var message = requiredFieldsValidator.GetMessageOfUnfilledFieldsList(requiredData, Environment.NewLine, ", ");
                    throw new ValidatorException(message);
                }
            } catch(APIException ex) {
                Logger.Error("Cannot validate required fields.", ex);
            }

            connector.Services.Save(asset);
            CommitEffort(efforts, asset);
        }

        /// <summary>
        /// Commit efforts.
        /// </summary>
        /// <param name="efforts">Recorded Effort collection</param>
        /// <param name="asset">Specific asset to commit related effort value.</param>
        private void CommitEffort(IDictionary<Asset, double> efforts, Asset asset) {
            if(!efforts.ContainsKey(asset)) {
                return;
            }

            var effortValue = efforts[asset];
            CreateEffort(asset, effortValue);
            efforts.Remove(asset);
        }

        private void CreateEffort(Asset asset, double effortValue) {
            var effort = connector.Services.New(effortType, asset.Oid);
            effort.SetAttributeValue(effortType.GetAttributeDefinition("Value"), effortValue);
            effort.SetAttributeValue(effortType.GetAttributeDefinition("Date"), DateTime.Now);
            connector.Services.Save(effort);
        }

        public void AddProperty(string attr, string prefix, bool isList) {
            AttributesToQuery.Add(new AttributeInfo(attr, prefix, isList));
        }

        public void ExecuteOperation(Asset asset, IOperation operation) {
            connector.Services.ExecuteOperation(operation, asset.Oid);
        }

        /// <summary>
        /// Refreshes data for Asset wrapped by specified Workitem.
        /// </summary>
        // TODO refactor
        public void RefreshAsset(Workitem workitem, IList<Asset> containingAssetCollection) {
            try {
                var stateDef = workitem.Asset.AssetType.GetAttributeDefinition("AssetState");
                
                var query = new Query(workitem.Asset.Oid.Momentless, false);
                AddSelection(query, workitem.TypePrefix);
                query.Selection.Add(stateDef);
                
                var newAssets = connector.Services.Retrieve(query);

                var containedIn = workitem.Parent == null ? containingAssetCollection : workitem.Parent.Asset.Children;

                if(newAssets.TotalAvaliable != 1) {
                    containedIn.Remove(workitem.Asset);
                    return;
                }

                var newAsset = newAssets.Assets[0];
                var newAssetState = (AssetState) newAsset.GetAttribute(stateDef).Value;
                
                if(newAssetState == AssetState.Closed) {
                    containedIn.Remove(workitem.Asset);
                    return;
                }

                containedIn[containedIn.IndexOf(workitem.Asset)] = newAsset;
                newAsset.Children.AddRange(workitem.Asset.Children);
            } catch(MetaException ex) {
                Logger.Error("Unable to get workitems.", ex);
            } catch(WebException ex) {
                connector.IsConnected = false;
                Logger.Error("Unable to get workitems.", ex);
            } catch(Exception ex) {
                Logger.Error("Unable to get workitems.", ex);
            }
        }

        public Workitem CreateWorkitem(string assetType, Workitem parent, IEntityContainer entityContainer) {
            var assetFactory = new AssetFactory(this, CurrentProject, LoggerFactory, AttributesToQuery);
            return WorkitemFactory.CreateWorkitem(assetFactory, assetType, parent, entityContainer);
        }
    }
}