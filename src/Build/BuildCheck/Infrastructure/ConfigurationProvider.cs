﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.BuildCheck.Infrastructure;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure.EditorConfig;
using Microsoft.Build.Experimental.BuildCheck.Utilities;

namespace Microsoft.Build.Experimental.BuildCheck.Infrastructure;

internal sealed class ConfigurationProvider : IConfigurationProvider
{
    private readonly EditorConfigParser _editorConfigParser = new EditorConfigParser();

    private const string BuildCheck_ConfigurationKey = "build_check";

    /// <summary>
    /// The dictionary used for storing the BuildCheckConfiguration per projectfile and rule id. The key is equal to {projectFullPath}-{ruleId}.
    /// </summary>
    private readonly ConcurrentDictionary<string, CheckConfiguration> _checkConfiguration = new ConcurrentDictionary<string, CheckConfiguration>(StringComparer.InvariantCultureIgnoreCase);

    /// <summary>
    /// The dictionary used for storing the key-value pairs retrieved from the .editorconfigs for specific projectfile. The key is equal to projectFullPath.
    /// </summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _editorConfigData = new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.InvariantCultureIgnoreCase);

    /// <summary>
    /// The dictionary used for storing the CustomConfigurationData per ruleId. The key is equal to ruleId.
    /// </summary>
    private readonly ConcurrentDictionary<string, CustomConfigurationData> _customConfigurationData = new ConcurrentDictionary<string, CustomConfigurationData>(StringComparer.InvariantCultureIgnoreCase);

    private readonly string[] _infrastructureConfigurationKeys = {
        BuildCheckConstants.scopeConfigurationKey,
        BuildCheckConstants.severityConfigurationKey,
    };

    /// <summary>
    /// Gets the user specified unrecognized configuration for the given check rule.
    /// 
    /// The configuration module should as well check that CustomConfigurationData
    ///  for a particular rule is equal across the whole build (for all projects)  - otherwise it should error out.
    /// This should apply to all rules for which the configuration is fetched.
    /// </summary>
    /// <param name="projectFullPath"></param>
    /// <param name="ruleId"></param>
    /// <returns></returns>
    public CustomConfigurationData GetCustomConfiguration(string projectFullPath, string ruleId)
    {
        var configuration = GetConfiguration(projectFullPath, ruleId);

        if (configuration is null)
        {
            return CustomConfigurationData.Null;
        }

        // remove the infrastructure owned key names
        foreach (var infraConfigurationKey in _infrastructureConfigurationKeys)
        {
            configuration.Remove(infraConfigurationKey);
        }

        if (!configuration.Any())
        {
            return CustomConfigurationData.Null;
        }

        var data = new CustomConfigurationData(ruleId, configuration);

        if (!_customConfigurationData.ContainsKey(ruleId))
        {
            _customConfigurationData[ruleId] = data;
        }

        return data;
    }

    /// <summary>
    /// Verifies if previously fetched custom configurations are equal to current one. 
    /// </summary>
    /// <param name="projectFullPath"></param>
    /// <param name="ruleId"></param>
    /// <throws><see cref="BuildCheckConfigurationException"/> If CustomConfigurationData differs in a build for a same ruleId</throws>
    /// <returns></returns>
    public void CheckCustomConfigurationDataValidity(string projectFullPath, string ruleId)
    {
        var configuration = GetCustomConfiguration(projectFullPath, ruleId);
        VerifyCustomConfigurationEquality(ruleId, configuration);
    }

    internal void VerifyCustomConfigurationEquality(string ruleId, CustomConfigurationData configurationData)
    {
        if (_customConfigurationData.TryGetValue(ruleId, out var storedConfiguration))
        {
            if (!storedConfiguration.Equals(configurationData))
            {
                throw new BuildCheckConfigurationException("Custom configuration should be equal between projects");
            }
        }
    }

    public CheckConfigurationEffective[] GetMergedConfigurations(
        string projectFullPath,
        Check check)
        => FillConfiguration(projectFullPath, check.SupportedRules, GetMergedConfiguration);

    public CheckConfiguration[] GetUserConfigurations(
        string projectFullPath,
        IReadOnlyList<string> ruleIds)
        => FillConfiguration(projectFullPath, ruleIds, GetUserConfiguration);

    /// <summary>
    /// Retrieve array of CustomConfigurationData for a given projectPath and ruleIds
    /// </summary>
    /// <param name="projectFullPath"></param>
    /// <param name="ruleIds"></param>
    /// <returns></returns>
    public CustomConfigurationData[] GetCustomConfigurations(
        string projectFullPath,
        IReadOnlyList<string> ruleIds)
        => FillConfiguration(projectFullPath, ruleIds, GetCustomConfiguration);

    public CheckConfigurationEffective[] GetMergedConfigurations(
        CheckConfiguration[] userConfigs,
        Check check)
    {
        var configurations = new CheckConfigurationEffective[userConfigs.Length];

        for (int idx = 0; idx < userConfigs.Length; idx++)
        {
            configurations[idx] = MergeConfiguration(
                check.SupportedRules[idx].Id,
                check.SupportedRules[idx].DefaultConfiguration,
                userConfigs[idx]);
        }

        return configurations;
    }

    private TConfig[] FillConfiguration<TConfig, TRule>(string projectFullPath, IReadOnlyList<TRule> ruleIds, Func<string, TRule, TConfig> configurationProvider)
    {
        TConfig[] configurations = new TConfig[ruleIds.Count];
        for (int i = 0; i < ruleIds.Count; i++)
        {
            configurations[i] = configurationProvider(projectFullPath, ruleIds[i]);
        }

        return configurations;
    }


    /// <summary>
    /// Generates a new dictionary that contains the key-value pairs from the original dictionary if the key starts with 'keyFilter'.
    /// If updateKey is set to 'true', the keys of the new dictionary will not include keyFilter.
    /// </summary>
    /// <param name="keyFilter"></param>
    /// <param name="originalConfiguration"></param>
    /// <param name="updateKey"></param>
    /// <returns></returns>
    private Dictionary<string, string> FilterDictionaryByKeys(string keyFilter, Dictionary<string, string> originalConfiguration, bool updateKey = false)
    {
        var filteredConfig = new Dictionary<string, string>();

        foreach (var kv in originalConfiguration)
        {
            if (kv.Key.StartsWith(keyFilter, StringComparison.OrdinalIgnoreCase))
            {
                var newKey = kv.Key;
                if (updateKey)
                {
                    newKey = kv.Key.Substring(keyFilter.Length);
                }

                filteredConfig[newKey] = kv.Value;
            }
        }

        return filteredConfig;
    }

    /// <summary>
    /// Fetches the .editorconfig data in form of Key-Value pair.
    /// Resulted dictionary will contain only BuildCheck related rules.
    /// </summary>
    /// <param name="projectFullPath"></param>
    /// <returns></returns>
    /// <exception cref="BuildCheckConfigurationException"></exception>
    private Dictionary<string, string> FetchEditorConfigRules(string projectFullPath)
    {
        var editorConfigRules = _editorConfigData.GetOrAdd(projectFullPath, (key) =>
        {
            Dictionary<string, string> config;
            try
            {
                config = _editorConfigParser.Parse(projectFullPath);
            }
            catch (Exception exception)
            {
                throw new BuildCheckConfigurationException($"Parsing editorConfig data failed", exception, BuildCheckConfigurationErrorScope.EditorConfigParser);
            }

            // clear the dictionary from the key-value pairs not BuildCheck related and
            // store the data so there is no need to parse the .editorconfigs all over again
            Dictionary<string, string> filteredData = FilterDictionaryByKeys($"{BuildCheck_ConfigurationKey}.", config);
            return filteredData;
        });

        return editorConfigRules;
    }

    internal Dictionary<string, string> GetConfiguration(string projectFullPath, string ruleId)
    {
        var config = FetchEditorConfigRules(projectFullPath);
        return FilterDictionaryByKeys($"{BuildCheck_ConfigurationKey}.{ruleId}.", config, updateKey: true);
    }

    /// <summary>
    /// Gets effective user specified (or default) configuration for the given check rule.
    /// The configuration values CAN be null upon this operation.
    /// 
    /// The configuration module should as well check that BuildCheckConfigurationInternal.EvaluationCheckScope
    ///  for all rules is equal - otherwise it should error out.
    /// </summary>
    /// <param name="projectFullPath"></param>
    /// <param name="ruleId"></param>
    /// <returns></returns>
    internal CheckConfiguration GetUserConfiguration(string projectFullPath, string ruleId)
    {
        var cacheKey = $"{ruleId}-{projectFullPath}";

        var editorConfigValue = _checkConfiguration.GetOrAdd(cacheKey, (key) =>
        {
            CheckConfiguration? editorConfig = CheckConfiguration.Null;
            editorConfig.RuleId = ruleId;
            var config = GetConfiguration(projectFullPath, ruleId);

            if (config.Any())
            {
                editorConfig = CheckConfiguration.Create(config);
            }

            return editorConfig;
        });

        return editorConfigValue;
    }

    /// <summary>
    /// Gets effective configuration for the given check rule.
    /// The configuration values are guaranteed to be non-null upon this merge operation.
    /// </summary>
    /// <param name="projectFullPath"></param>
    /// <param name="checkRule"></param>
    /// <returns></returns>
    internal CheckConfigurationEffective GetMergedConfiguration(string projectFullPath, CheckRule checkRule)
        => GetMergedConfiguration(projectFullPath, checkRule.Id, checkRule.DefaultConfiguration);

    internal CheckConfigurationEffective MergeConfiguration(
        string ruleId,
        CheckConfiguration defaultConfig,
        CheckConfiguration editorConfig)
        => new CheckConfigurationEffective(
            ruleId: ruleId,
            evaluationCheckScope: GetConfigValue(editorConfig, defaultConfig, cfg => cfg.EvaluationCheckScope),
            severity: GetSeverityValue(editorConfig, defaultConfig));

    private CheckConfigurationEffective GetMergedConfiguration(
        string projectFullPath,
        string ruleId,
        CheckConfiguration defaultConfig)
        => MergeConfiguration(ruleId, defaultConfig, GetUserConfiguration(projectFullPath, ruleId));

    private T GetConfigValue<T>(
        CheckConfiguration editorConfigValue,
        CheckConfiguration defaultValue,
        Func<CheckConfiguration, T?> propertyGetter) where T : struct
        => propertyGetter(editorConfigValue) ??
           propertyGetter(defaultValue) ??
           EnsureNonNull(propertyGetter(CheckConfiguration.Default));

    private CheckResultSeverity GetSeverityValue(CheckConfiguration editorConfigValue, CheckConfiguration defaultValue)
    {
        CheckResultSeverity? resultSeverity = null;

        // Consider Default as null, so the severity from the default value could be selected.
        // Default severity is not recognized by the infrastructure and serves for configuration purpuses only. 
        if (editorConfigValue.Severity != null && editorConfigValue.Severity != CheckResultSeverity.Default)
        {
            resultSeverity = editorConfigValue.Severity;
        }

        resultSeverity ??= defaultValue.Severity ?? EnsureNonNull(CheckConfiguration.Default.Severity);

        return resultSeverity.Value;
    }

    private static T EnsureNonNull<T>(T? value) where T : struct
    {
        if (value is null)
        {
            throw new InvalidOperationException("Value is null");
        }

        return value.Value;
    }
}
