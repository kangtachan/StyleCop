// --------------------------------------------------------------------------------------------------------------------
// <copyright file="HighlightingRegistering.cs" company="http://stylecop.codeplex.com">
//   MS-PL
// </copyright>
// <license>
//   This source code is subject to terms and conditions of the Microsoft 
//   Public License. A copy of the license can be found in the License.html 
//   file at the root of this distribution. If you cannot locate the  
//   Microsoft Public License, please send an email to dlr@microsoft.com. 
//   By using this source code in any fashion, you are agreeing to be bound 
//   by the terms of the Microsoft Public License. You must not remove this 
//   notice, or any other, from this software.
// </license>
// <summary>
//   Registers StyleCop Highlighters to allow their severity to be set.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace StyleCop.ReSharper.Options
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    using JetBrains.Application;
    using JetBrains.Application.Catalogs;
    using JetBrains.Application.Environment;
    using JetBrains.Application.FileSystemTracker;
    using JetBrains.Application.Parts;
    using JetBrains.Application.Settings;
    using JetBrains.DataFlow;
    using JetBrains.Metadata.Utils;
    using JetBrains.ReSharper.Feature.Services.Daemon;
    using JetBrains.ReSharper.Psi;
    using JetBrains.ReSharper.Psi.CSharp;
    using JetBrains.Util.dataStructures.Sources;

    using StyleCop.ReSharper.Core;

    /// <summary>
    /// Registers StyleCop Highlighters to allow their severity to be set.
    /// </summary>
    [ShellComponent]
    public class HighlightingRegistering : ICustomConfigurableSeverityItemProvider
    {
        private readonly Lifetime lifetime;

        /// <summary>
        /// The template to be used for the group title.
        /// </summary>
        private const string GroupTitleTemplate = "StyleCop - {0}";

        /// <summary>
        /// The template to be used for the highlight ID's.
        /// </summary>
        private const string HighlightIdTemplate = "StyleCop.{0}";

        private readonly IPartCatalogSet partsCatalogueSet;

        /// <summary>
        /// Initializes a new instance of the HighlightingRegistering class.
        /// </summary>
        /// <param name="lifetime">Lifetime of the component</param>
        /// <param name="jetEnvironment">The environment</param>
        /// <param name="settingsStore">The settings store.</param>
        /// <param name="fileSystemTracker">
        /// The file System Tracker.
        /// </param>
        public HighlightingRegistering(Lifetime lifetime, JetEnvironment jetEnvironment, ISettingsStore settingsStore, IFileSystemTracker fileSystemTracker)
        {
            this.lifetime = lifetime;
            this.partsCatalogueSet = jetEnvironment.FullPartCatalogSet;

            // TODO: We shouldn't be doing any of this at runtime, especially not on each load
            // Registering highlightings should happen declaratively
            // Create this instance directly, rather than use the pool, because the pool needs to
            // be per-solution, as it caches settings for files in the solution
            Lifetimes.Using(
                temporaryLifetime =>
                    {
                        // We don't really need the file system tracker - it's only used when we get
                        // settings, which we don't do as part of highlighting initialisation
                        StyleCopCore core = StyleCopCoreFactory.Create(temporaryLifetime, settingsStore, fileSystemTracker);
                        this.Register(core);
                    });
        }

        /// <summary>
        /// Gets the highlight ID for this rule.
        /// </summary>
        /// <param name="ruleID">
        /// The rule ID.
        /// </param>
        /// <returns>
        /// The highlight ID.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// RuleID is null.
        /// </exception>
        public static string GetHighlightID(string ruleID)
        {
            if (string.IsNullOrEmpty(ruleID))
            {
                throw new ArgumentNullException("ruleID");
            }

            return string.Format(HighlightIdTemplate, ruleID);
        }

        private static string SplitCamelCase(string input)
        {
            return Regex.Replace(input, "([A-Z])", " $1", RegexOptions.Compiled).Trim();
        }

        private void Register(StyleCopCore core)
        {
            Dictionary<SourceAnalyzer, List<StyleCopRule>> analyzerRulesDictionary = StyleCopRule.GetRules(core);

            // ICustomConfigurableSeverityItemProvider allows us to add the items, but not groups. We'll
            // cheat at adding groups by creating a fake catalogue that is loaded into the container.
            // When the HighlightingSettingsManager sees the new catalogue it pulls the group attributes
            // from it and registers them. We can't use this to register the items, as there's a bug in
            // the catalogue builders that means we can't add enums (they need to be ulong when they're
            // retrieved, but there's a check to verify that the expected type (int) is compatible, and
            // it isn't. This bug isn't affected by the group name, which is just a string)
            IPartsCatalogue catalogue = this.CreateFakeCatalogue(analyzerRulesDictionary);
            PartCatalog catalog = catalogue.WrapLegacy();
            this.partsCatalogueSet.Catalogs.Add(this.lifetime, catalog);

            var configurableSeverityItems = new List<Tuple<PsiLanguageType, ConfigurableSeverityItem>>();
            foreach (KeyValuePair<SourceAnalyzer, List<StyleCopRule>> analyzerRule in analyzerRulesDictionary)
            {
                string analyzerName = analyzerRule.Key.Name;
                string groupName = string.Format(GroupTitleTemplate, analyzerName);

                foreach (StyleCopRule rule in analyzerRule.Value)
                {
                    string ruleName = rule.RuleID + ": " + SplitCamelCase(rule.Name);
                    string highlightID = GetHighlightID(rule.RuleID);
                    ConfigurableSeverityItem severityItem = new ConfigurableSeverityItem(
                        highlightID,
                        null,
                        groupName,
                        ruleName,
                        rule.Description,
                        Severity.WARNING,
                        false,
                        false,
                        null);
                    configurableSeverityItems.Add(Tuple.Create((PsiLanguageType)CSharpLanguage.Instance, severityItem));
                }
            }
            this.ConfigurableSeverityItems = configurableSeverityItems;
        }

        private IPartsCatalogue CreateFakeCatalogue(Dictionary<SourceAnalyzer, List<StyleCopRule>> analyzerRules)
        {
            IPartCatalogueFactory factory = new FlyweightPartFactory();

            PartCatalogueAssembly assembly = new PartCatalogueAssembly(AssemblyNameInfo.Create("StyleCopHighlightings", null), null);

            List<PartCatalogueAttribute> assemblyAttributes = new List<PartCatalogueAttribute>();
            foreach (KeyValuePair<SourceAnalyzer, List<StyleCopRule>> analyzerRule in analyzerRules)
            {
                string analyzerName = analyzerRule.Key.Name;
                string groupName = string.Format(GroupTitleTemplate, analyzerName);

                var groupAttribute = new RegisterConfigurableHighlightingsGroupAttribute(groupName, groupName);
                assemblyAttributes.Add(CreatePartAttribute(groupAttribute, factory));
            }

            assembly.AssignAttributes(assemblyAttributes);

            IList<PartCatalogueAssembly> assemblies = new List<PartCatalogueAssembly>()
                                                          {
                                                              assembly
                                                          };

            IList<PartCatalogueType> parts = new List<PartCatalogueType>();
            return new PartsCatalogue(parts, assemblies);
        }

        private static PartCatalogueAttribute CreatePartAttribute(Attribute attribute, IPartCatalogueFactory factory)
        {
            // OK, this is getting out of hand. It seemed like a good idea to replace
            // reflection with a fake catalogue, but ReSharper 10 has changed things.
            // Implementing your own catalogue is non-trivial, and the wrapper for
            // legacy catalogues has issues with attribute properties (including ctor
            // args). It expects a string instance to be represented with a StringSource,
            // and tries to unbox an enum directly into a ulong, which fails. We can work
            // around these by replacing the actual value in the properties with one that
            // will work. But it also tries to unbox a bool into a ulong, which also fails,
            // and we can't work around that (it asserts that the value in the property is
            // also a bool). So, we totally fudge it. It just so happens that the values
            // we want to use are false, and HighlightSettingsManagerImpl will handle
            // missing values nicely, defaulting to false. So, rip em out.
            // Perhaps reflection was the better idea... (well, strictly speaking not having
            // "dynamically" loaded highlightings is a better idea!)
            var originalPartAttribute = PartHelpers.CreatePartAttribute(attribute, factory);
            var newProperties = from p in originalPartAttribute.GetProperties()
                                where !(p.Value is bool && (bool)p.Value == false)
                                select new PartCatalogueAttributeProperty(p.Name, WrapValue(p.Value), p.Disposition);

            return new PartCatalogueAttribute(originalPartAttribute.Type, newProperties, originalPartAttribute.ConstructorFormalParameterTypes);
        }

        private static object WrapValue(object value)
        {
            var s = value as string;
            if (s != null)
            {
                StringSource ss = s;
                return ss;
            }

            return value;
        }

        public IEnumerable<Tuple<PsiLanguageType, ConfigurableSeverityItem>> ConfigurableSeverityItems { get; private set; }
    }
}