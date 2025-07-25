﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Beatmaps.Drawables.Cards;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;
using osu.Game.Overlays.BeatmapSet.Buttons;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Overlays.BeatmapSet
{
    public partial class BeatmapSetHeaderContent : CompositeDrawable
    {
        public readonly Bindable<APIBeatmapSet> BeatmapSet = new Bindable<APIBeatmapSet>();

        private const float transition_duration = 200;
        private const float buttons_height = 45;
        private const float buttons_spacing = 5;

        public bool DownloadButtonsVisible => downloadButtonsContainer.Any();

        public readonly Details Details;
        public readonly BeatmapPicker Picker;

        private readonly UpdateableOnlineBeatmapSetCover cover;
        private readonly Box coverGradient;
        private readonly LinkFlowContainer title, artist;
        private readonly AuthorInfo author;
        private readonly VideoIconPill videoIconPill;
        private readonly StoryboardIconPill storyboardIconPill;

        private ExternalLinkButton externalLink;

        private readonly FillFlowContainer downloadButtonsContainer;
        private readonly BeatmapAvailability beatmapAvailability;
        private readonly BeatmapSetOnlineStatusPill onlineStatusPill;
        private readonly FavouriteButton favouriteButton;
        private readonly FillFlowContainer fadeContent;
        private readonly LoadingSpinner loading;

        private BeatmapDownloadTracker downloadTracker;

        [Resolved]
        private IAPIProvider api { get; set; }

        [Resolved]
        private BeatmapRulesetSelector rulesetSelector { get; set; }

        public BeatmapSetHeaderContent()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Children = new Drawable[]
                        {
                            cover = new UpdateableOnlineBeatmapSetCover
                            {
                                RelativeSizeAxes = Axes.Both,
                                Masking = true,
                            },
                            coverGradient = new Box
                            {
                                RelativeSizeAxes = Axes.Both
                            },
                        },
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding
                        {
                            Vertical = BeatmapSetOverlay.Y_PADDING,
                            Left = WaveOverlayContainer.HORIZONTAL_PADDING,
                            Right = WaveOverlayContainer.HORIZONTAL_PADDING + BeatmapSetOverlay.RIGHT_WIDTH + 10,
                        },
                        Children = new Drawable[]
                        {
                            fadeContent = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Children = new Drawable[]
                                {
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Child = Picker = new BeatmapPicker(),
                                    },
                                    title = new MetadataFlowContainer(s =>
                                    {
                                        s.Font = OsuFont.GetFont(size: 30, weight: FontWeight.SemiBold, italics: true);
                                    })
                                    {
                                        Margin = new MarginPadding { Top = 15 },
                                    },
                                    artist = new MetadataFlowContainer(s =>
                                    {
                                        s.Font = OsuFont.GetFont(size: 20, weight: FontWeight.Medium, italics: true);
                                    })
                                    {
                                        Margin = new MarginPadding { Bottom = 20 },
                                    },
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Child = author = new AuthorInfo(),
                                    },
                                    beatmapAvailability = new BeatmapAvailability(),
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = buttons_height,
                                        Margin = new MarginPadding { Top = 10 },
                                        Children = new Drawable[]
                                        {
                                            favouriteButton = new FavouriteButton
                                            {
                                                BeatmapSet = { BindTarget = BeatmapSet }
                                            },
                                            downloadButtonsContainer = new FillFlowContainer
                                            {
                                                RelativeSizeAxes = Axes.Both,
                                                Padding = new MarginPadding { Left = buttons_height + buttons_spacing },
                                                Spacing = new Vector2(buttons_spacing),
                                            },
                                        }
                                    },
                                },
                            },
                        }
                    },
                    loading = new LoadingSpinner
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Scale = new Vector2(1.5f),
                    },
                    new FillFlowContainer
                    {
                        Anchor = Anchor.BottomRight,
                        Origin = Anchor.BottomRight,
                        AutoSizeAxes = Axes.Both,
                        Margin = new MarginPadding { Top = BeatmapSetOverlay.Y_PADDING, Right = WaveOverlayContainer.HORIZONTAL_PADDING },
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(10),
                        Children = new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Y,
                                RelativeSizeAxes = Axes.X,
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(10),
                                Children = new Drawable[]
                                {
                                    onlineStatusPill = new BeatmapSetOnlineStatusPill
                                    {
                                        Anchor = Anchor.TopRight,
                                        Origin = Anchor.TopRight,
                                        TextSize = 14,
                                        TextPadding = new MarginPadding { Horizontal = 35, Vertical = 10 }
                                    },
                                    storyboardIconPill = new StoryboardIconPill
                                    {
                                        AutoSizeAxes = Axes.X,
                                        RelativeSizeAxes = Axes.Y,
                                        Anchor = Anchor.TopRight,
                                        Origin = Anchor.TopRight,
                                        IconSize = new Vector2(34),
                                        IconPadding = new MarginPadding(10),
                                    },
                                    videoIconPill = new VideoIconPill
                                    {
                                        AutoSizeAxes = Axes.X,
                                        RelativeSizeAxes = Axes.Y,
                                        Anchor = Anchor.TopRight,
                                        Origin = Anchor.TopRight,
                                        IconSize = new Vector2(34),
                                        IconPadding = new MarginPadding(10),
                                    },
                                }
                            },

                            Details = new Details(),
                        },
                    },
                }
            };

            Picker.Beatmap.ValueChanged += b =>
            {
                Details.BeatmapInfo = b.NewValue;
                updateExternalLink();

                onlineStatusPill.Status = b.NewValue?.Status ?? BeatmapOnlineStatus.None;
            };
        }

        private void updateExternalLink()
        {
            if (externalLink != null)
                externalLink.Link = Picker.Beatmap.Value?.GetOnlineURL(api) ?? BeatmapSet.Value?.GetOnlineURL(api);
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            coverGradient.Colour = ColourInfo.GradientVertical(colourProvider.Background6.Opacity(0.3f), colourProvider.Background6.Opacity(0.8f));

            BeatmapSet.BindValueChanged(setInfo =>
            {
                Picker.BeatmapSet = rulesetSelector.BeatmapSet = author.BeatmapSet = beatmapAvailability.BeatmapSet = Details.BeatmapSet = setInfo.NewValue;
                cover.OnlineInfo = setInfo.NewValue;

                downloadTracker?.RemoveAndDisposeImmediately();

                if (setInfo.NewValue == null)
                {
                    onlineStatusPill.FadeTo(0.5f, 500, Easing.OutQuint);
                    videoIconPill.Hide();
                    storyboardIconPill.Hide();
                    fadeContent.Hide();

                    loading.Show();

                    downloadButtonsContainer.FadeOut(transition_duration);
                    favouriteButton.FadeOut(transition_duration);
                }
                else
                {
                    downloadTracker = new BeatmapDownloadTracker(setInfo.NewValue);
                    downloadTracker.State.BindValueChanged(_ => updateDownloadButtons());
                    AddInternal(downloadTracker);

                    fadeContent.FadeIn(500, Easing.OutQuint);

                    loading.Hide();

                    if (setInfo.NewValue.HasVideo)
                        videoIconPill.Show();
                    else
                        videoIconPill.Hide();

                    if (setInfo.NewValue.HasStoryboard)
                        storyboardIconPill.Show();
                    else
                        storyboardIconPill.Hide();

                    var titleText = new RomanisableString(setInfo.NewValue.TitleUnicode, setInfo.NewValue.Title);
                    var artistText = new RomanisableString(setInfo.NewValue.ArtistUnicode, setInfo.NewValue.Artist);

                    title.Clear();
                    artist.Clear();

                    title.AddLink(titleText, LinkAction.SearchBeatmapSet, LocalisableString.Interpolate($@"title=""""{titleText}"""""));

                    title.AddArbitraryDrawable(Empty().With(d => d.Width = 5));
                    title.AddArbitraryDrawable(externalLink = new ExternalLinkButton());

                    if (setInfo.NewValue.HasExplicitContent)
                    {
                        title.AddArbitraryDrawable(Empty().With(d => d.Width = 10));
                        title.AddArbitraryDrawable(new ExplicitContentBeatmapBadge());
                    }

                    if (setInfo.NewValue.FeaturedInSpotlight)
                    {
                        title.AddArbitraryDrawable(Empty().With(d => d.Width = 10));
                        title.AddArbitraryDrawable(new SpotlightBeatmapBadge());
                    }

                    artist.AddLink(artistText, LinkAction.SearchBeatmapSet, LocalisableString.Interpolate($@"artist=""""{artistText}"""""));

                    if (setInfo.NewValue.TrackId != null)
                    {
                        artist.AddArbitraryDrawable(Empty().With(d => d.Width = 10));
                        artist.AddArbitraryDrawable(new FeaturedArtistBeatmapBadge());
                    }

                    updateExternalLink();

                    onlineStatusPill.FadeIn(500, Easing.OutQuint);

                    downloadButtonsContainer.FadeIn(transition_duration);
                    favouriteButton.FadeIn(transition_duration);

                    updateDownloadButtons();
                }
            }, true);
        }

        private void updateDownloadButtons()
        {
            if (BeatmapSet.Value == null) return;

            if (BeatmapSet.Value.Availability.DownloadDisabled && downloadTracker.State.Value != DownloadState.LocallyAvailable)
            {
                downloadButtonsContainer.Clear();
                return;
            }

            switch (downloadTracker.State.Value)
            {
                case DownloadState.LocallyAvailable:
                    // temporary for UX until new design is implemented.
                    downloadButtonsContainer.Child = new BeatmapDownloadButton(BeatmapSet.Value)
                    {
                        Width = 50,
                        RelativeSizeAxes = Axes.Y,
                        SelectedBeatmap = { BindTarget = Picker.Beatmap }
                    };
                    break;

                case DownloadState.Downloading:
                case DownloadState.Importing:
                    // temporary to avoid showing two buttons for maps with novideo. will be fixed in new beatmap overlay design.
                    downloadButtonsContainer.Child = new HeaderDownloadButton(BeatmapSet.Value);
                    break;

                default:
                    downloadButtonsContainer.Child = new HeaderDownloadButton(BeatmapSet.Value);
                    if (BeatmapSet.Value.HasVideo)
                        downloadButtonsContainer.Add(new HeaderDownloadButton(BeatmapSet.Value, true));
                    break;
            }
        }

        public partial class MetadataFlowContainer : LinkFlowContainer
        {
            public MetadataFlowContainer(Action<SpriteText> defaultCreationParameters = null)
                : base(defaultCreationParameters)
            {
                TextAnchor = Anchor.CentreLeft;
                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;
            }

            protected override DrawableLinkCompiler CreateLinkCompiler(ITextPart textPart) => new MetadataLinkCompiler(textPart);

            public partial class MetadataLinkCompiler : DrawableLinkCompiler
            {
                public MetadataLinkCompiler(ITextPart part)
                    : base(part)
                {
                }

                [BackgroundDependencyLoader]
                private void load()
                {
                    IdleColour = Color4.White;
                }
            }
        }
    }
}
