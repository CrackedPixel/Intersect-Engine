﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Intersect.Extensions;
using Intersect.Logging;
using Intersect.Utilities;

using JetBrains.Annotations;

using Newtonsoft.Json;

namespace Intersect.Core.ExperimentalFeatures
{
    public abstract partial class CommonExperiments<TExperiments> : IFlagProvider
        where TExperiments : CommonExperiments<TExperiments>
    {
        private static readonly Guid NamespaceId = Guid.Parse("c68012b3-d666-4204-84eb-4976f2b570ab");

        [NotNull] private readonly IDictionary<Guid, PropertyInfo> mFlagsById;

        [NotNull] private readonly IDictionary<string, PropertyInfo> mFlagsByName;

        protected CommonExperiments()
        {
            mFlagsById = new Dictionary<Guid, PropertyInfo>();
            mFlagsByName = new Dictionary<string, PropertyInfo>();

            RegisterPropertiesAndAliases();
        }

        private void RegisterPropertiesAndAliases()
        {
            PropertyInfo existingFlag;
            var experimentsType = typeof(TExperiments);
            var aliasProperties = new List<(PropertyInfo property, ExperimentalFlagAliasAttribute aliasAttribute)>();
            var properties =
                experimentsType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var flagProperties = properties
                .Where(property => typeof(IExperimentalFlag).IsAssignableFrom(property.PropertyType))
                .ToList();

            flagProperties.ForEach(
                property =>
                {
                    var aliasAttribute = property?.GetCustomAttribute<ExperimentalFlagAliasAttribute>(true);
                    if (aliasAttribute != null)
                    {
                        aliasProperties.Add((property, aliasAttribute));
                        return;
                    }

                    if (property?.DeclaringType == null)
                    {
                        throw new InvalidOperationException();
                    }

                    var flagName = property.Name.ToLowerInvariant();
                    var namespaceId = GetNamespaceIdFor(property.DeclaringType);
                    var flagId = GuidUtils.CreateNamed(namespaceId, property.Name);

                    if (TryGetProperty(flagName, out existingFlag))
                    {
                        throw new InvalidOperationException(
                            $@"Tried to add a flag with name '{flagName}' in '{property.DeclaringType?.FullName}' but there is already a flag with that name defined in '{existingFlag.DeclaringType?.FullName}'."
                        );
                    }

                    mFlagsById.Add(flagId, property);
                    mFlagsByName.Add(flagName, property);
                }
            );

            aliasProperties.ForEach(
                pair =>
                {
                    var (property, aliasAttribute) = pair;
                    var aliasName = property.Name.ToLowerInvariant();
                    var targetName = aliasAttribute.Of;
                    var alias = new ExperimentalFlagAlias(this, targetName, aliasName);
                    property.SetValue(this, alias);

                    if (TryGetProperty(aliasName, out existingFlag))
                    {
                        throw new InvalidOperationException(
                            $@"Tried to add an alias with name '{aliasName}' in '{property.DeclaringType?.FullName}' but there is already a flag with that name defined in '{existingFlag.DeclaringType?.FullName}'."
                        );
                    }

                    mFlagsByName.Add(aliasName, property);
                }
            );
        }

        public bool Disable(IExperimentalFlag flag) => TrySet(flag, false);

        public bool Disable(Guid flagId) => TrySet(flagId, false);

        public bool Disable([NotNull] string flagName) => TrySet(flagName, false);

        public bool Enable([NotNull] IExperimentalFlag flag) => TrySet(flag, true);

        public bool Enable(Guid flagId) => TrySet(flagId, true);

        public bool Enable([NotNull] string flagName) => TrySet(flagName, true);

        public bool IsEnabled(Guid flagId) =>
            mFlagsById.TryGetValue(flagId, out var property) &&
            property.GetValue(this) is IExperimentalFlag flag &&
            flag.Enabled;

        public bool IsEnabled(string flagName) =>
            mFlagsByName.TryGetValue(flagName, out var property) &&
            property.GetValue(this) is IExperimentalFlag flag &&
            flag.Enabled;

        public bool TryGet(Guid flagId, out IExperimentalFlag flag) =>
            ValueUtils.SetDefault(TryGetProperty(flagId, out var property), out flag) &&
            property.TryGetValue(this, out flag);

        public bool TryGet(string flagName, out IExperimentalFlag flag) =>
            ValueUtils.SetDefault(TryGetProperty(flagName, out var property), out flag) &&
            property.TryGetValue(this, out flag);

        protected bool TryGetProperty(IExperimentalFlag flag, out PropertyInfo flagPropertyInfo) =>
            mFlagsById.TryGetValue(flag.Guid, out flagPropertyInfo);

        protected bool TryGetProperty(Guid flagId, out PropertyInfo flagPropertyInfo) =>
            mFlagsById.TryGetValue(flagId, out flagPropertyInfo);

        protected bool TryGetProperty([NotNull] string flagName, out PropertyInfo flagPropertyInfo) =>
            ValueUtils.SetDefault(!string.IsNullOrWhiteSpace(flagName), out flagPropertyInfo) &&
            mFlagsByName.TryGetValue(flagName.ToLowerInvariant(), out flagPropertyInfo);

        private bool InternalTrySet(PropertyInfo property, [NotNull] IExperimentalFlag flag, bool enabled)
        {
            /* Unwraps the flag */
            if (flag is ExperimentalFlagAlias && !TryGet(flag.Guid, out flag))
            {
                return false;
            }

            if (property == null && !TryGetProperty(flag, out property))
            {
                return false;
            }

            property.SetValue(this, flag.With(enabled));
            Save();
            return true;
        }

        public bool TrySet([NotNull] IExperimentalFlag flag, bool enabled) => InternalTrySet(null, flag, enabled);

        public bool TrySet(Guid flagId, bool enabled) =>
            TryGetProperty(flagId, out var property) &&
            property.GetValue(this) is IExperimentalFlag flag &&
            InternalTrySet(property, flag, enabled);

        public bool TrySet([NotNull] string flagName, bool enabled) =>
            TryGetProperty(flagName, out var property) &&
            property.GetValue(this) is IExperimentalFlag flag &&
            InternalTrySet(property, flag, enabled);

        protected virtual bool Load()
        {
            if (!File.Exists(CONFIG_PATH))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(CONFIG_PATH, Encoding.UTF8);
                JsonConvert.PopulateObject(
                    json, Instance, new JsonSerializerSettings
                    {
                        Converters = new List<JsonConverter>
                        {
                            new ExperimentalFlagConverter()
                        }
                    }
                );

                return true;
            }
            catch (Exception exception)
            {
                Log.Error(exception);
                return false;
            }
        }

        protected virtual void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(CONFIG_PATH) ?? "resources/config";
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(CONFIG_PATH, json, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                Log.Error(exception);
            }
        }
    }
}
