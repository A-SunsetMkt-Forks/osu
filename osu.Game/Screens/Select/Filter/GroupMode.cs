﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel;

namespace osu.Game.Screens.Select.Filter
{
    public enum GroupMode
    {
        [Description("None")]
        None,

        [Description("Artist")]
        Artist,

        [Description("Author")]
        Author,

        [Description("BPM")]
        BPM,

        // [Description("Collections")]
        // Collections,

        [Description("Date Added")]
        DateAdded,

        [Description("Date Ranked")]
        DateRanked,

        [Description("Difficulty")]
        Difficulty,

        // [Description("Favourites")]
        // Favourites,

        [Description("Last Played")]
        LastPlayed,

        [Description("Length")]
        Length,

        // [Description("My Maps")]
        // MyMaps,

        // [Description("Rank Achieved")]
        // RankAchieved,

        [Description("Ranked Status")]
        RankedStatus,

        [Description("Source")]
        Source,

        [Description("Title")]
        Title,
    }
}
