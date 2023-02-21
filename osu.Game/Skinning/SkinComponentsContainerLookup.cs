// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Rulesets;

namespace osu.Game.Skinning
{
    /// <summary>
    /// Represents a lookup of a collection of elements that make up a particular skinnable <see cref="TargetArea"/> of the game.
    /// </summary>
    public class SkinComponentsContainerLookup : ISkinComponentLookup
    {
        /// <summary>
        /// The target area / layer of the game for which skin components will be returned.
        /// </summary>
        public readonly TargetArea Target;

        /// <summary>
        /// The ruleset for which skin components should be returned.
        /// A <see langword="null"/> value means that returned components are global and should be applied for all rulesets.
        /// </summary>
        public readonly RulesetInfo? Ruleset;

        public SkinComponentsContainerLookup(TargetArea target, RulesetInfo? ruleset = null)
        {
            Target = target;
            Ruleset = ruleset;
        }

        /// <summary>
        /// Represents a particular area or part of a game screen whose layout can be customised using the skin editor.
        /// </summary>
        public enum TargetArea
        {
            MainHUDComponents,
            SongSelect
        }
    }
}