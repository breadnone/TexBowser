using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using LLama;
using LLama.Common;
using LLama.Sampling;
using MdXaml;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using System.Windows.Markup;

namespace TexBowser
{
    public class AppSettings
    {
        public string ModelPath { get; set; } = "";
        public uint ContextSize { get; set; } = 100000;
        public int GpuLayerCount { get; set; } = 0;
        public int MainGpuId { get; set; } = 0;
        public bool UseFlashAttention { get; set; } = false;
        public bool UseMemorymap { get; set; } = true;
        public int MaxHtmlChars { get; set; } = 50000;
        public int MaxLLMOutputTokens { get; set; } = 8192;
        public List<string> Bookmarks { get; set; } = new List<string>();

        public float Temperature { get; set; } = 0.8f;
        public int TopK { get; set; } = 40;
        public float TopP { get; set; } = 0.9f;
        public float RepeatPenalty { get; set; } = 1.1f;
    }

    public class HistoryEntry
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string MarkdownText { get; set; }
        public List<(string text, string url)> Links { get; set; } = new List<(string, string)>();
    }

    public class BrowserTab
    {
        public string Title { get; set; } = "New Tab";
        public string CurrentUrl { get; set; } = "";
        public string Mode { get; set; } = "web";
        public string RawHtml { get; set; } = "";
        public HistoryEntry CurrentPage { get; set; }
        public Stack<HistoryEntry> History { get; set; } = new Stack<HistoryEntry>();
        public Stack<HistoryEntry> ForwardHistory { get; set; } = new Stack<HistoryEntry>();
        public List<(string text, string url)> Links { get; set; } = new List<(string, string)>();
        public StringBuilder ConsoleBuffer { get; set; } = new StringBuilder();
        public volatile bool IsProcessing = false;
        public CancellationTokenSource Cts = null;
        public Border TabUI { get; set; }
        public TextBlock TabTitle { get; set; }
    }

    public partial class MainWindow : Window
    {
        private static readonly Color BgColor = Color.FromRgb(69, 59, 46);
        private static readonly Color TextColor = Color.FromRgb(209, 171, 117);
        private static readonly Color AccentColor = Color.FromRgb(0, 220, 120);
        private static readonly Color PanelColor = Color.FromRgb(51, 45, 35);
        private static readonly Color OutlineColor = Color.FromRgb(120, 100, 80);
        private static readonly FontFamily AppFont = new FontFamily("Consolas");

        // Cached/frozen brushes + styles + a single reused Markdown engine
        // for RenderConsole. Previously these were all reallocated on
        // every single render (every AppendOutput call), which was one of
        // the biggest sources of allocation churn in the app.
        private static readonly Brush BgBrush = MakeFrozen(BgColor);
        private static readonly Brush TextBrush = MakeFrozen(TextColor);
        private static readonly Brush PanelBrush = MakeFrozen(PanelColor);
        private static readonly Brush TableCellBorderBrush = MakeFrozen(Color.FromRgb(100, 90, 70));
        private static readonly Brush LinkBrush = MakeFrozen(Color.FromRgb(50, 180, 255));

        private static readonly Style TableStyle = CreateFrozenStyle(typeof(Table), new[]
        {
            new Setter(Table.BackgroundProperty, PanelBrush)
        });
        private static readonly Style TableCellStyle = CreateFrozenStyle(typeof(TableCell), new[]
        {
            new Setter(TableCell.BackgroundProperty, PanelBrush),
            new Setter(TableCell.BorderBrushProperty, TableCellBorderBrush),
            new Setter(TableCell.ForegroundProperty, TextBrush)
        });
        private static readonly Style HyperlinkStyle = CreateFrozenStyle(typeof(Hyperlink), new[]
        {
            new Setter(Hyperlink.ForegroundProperty, LinkBrush),
            new Setter(Hyperlink.TextDecorationsProperty, null)
        });

        private static Brush MakeFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Style CreateFrozenStyle(Type targetType, Setter[] setters)
        {
            var style = new Style(targetType);
            foreach (var s in setters) style.Setters.Add(s);
            style.Seal();
            return style;
        }

        // Markdown engine holds no per-document state that matters here;
        // reusing one instance avoids reconstructing it on every render.
        private readonly Markdown _markdownEngine = new Markdown();

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private void EnableDarkModeTitlebar()
        {
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            int trueValue = 1;
            int result = DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref trueValue, sizeof(int));
            if (result != 0)
            {
                DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref trueValue, sizeof(int));
            }
        }

        private readonly Grid _rootGrid;
        private readonly Grid _outputGrid;
        private readonly RichTextBox _outputBox;
        private readonly TextBox _inputBox;
        private readonly ComboBox _modeComboBox;
        private readonly TextBlock _spinnerBlock;
        private readonly Button _btnPrev;
        private readonly Button _btnNext;
        private readonly Button _btnStop;
        private readonly Button _btnBookmarks;
        private readonly ContextMenu _bookmarkMenu;
        private readonly ContextMenu _outputContextMenu;
        private readonly StackPanel _tabPanel;
        private readonly Button _btnAddTab;

        private readonly Border _searchPanel;
        private readonly TextBox _searchBox;
        private readonly Button _btnSearchClose;
        private readonly List<TextRange> _highlightedRanges = new List<TextRange>();

        private readonly HttpClient _httpClient = new HttpClient();
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private BrowserTab _activeTab = null;

        private LLamaWeights _model;
        private ModelParams _modelParams;
        private StatelessExecutor _executor;
        private volatile bool _modelLoaded = false;

        private AppSettings _settings;
        private readonly string _settingsPath;

        public MainWindow()
        {
            InitializeComponent();
            EnableDarkModeTitlebar();

            string menuStylesXaml = @"
            <ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                <Style TargetType=""ContextMenu"">
                    <Setter Property=""Background"" Value=""#332D23"" />
                    <Setter Property=""BorderBrush"" Value=""#786450"" />
                    <Setter Property=""Foreground"" Value=""#D1AB75"" />
                    <Setter Property=""Padding"" Value=""2"" />
                    <Setter Property=""Template"">
                        <Setter.Value>
                            <ControlTemplate TargetType=""ContextMenu"">
                                <Border Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""1"" CornerRadius=""2"">
                                    <ItemsPresenter />
                                </Border>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style TargetType=""MenuItem"">
                    <Setter Property=""Background"" Value=""Transparent"" />
                    <Setter Property=""Foreground"" Value=""#D1AB75"" />
                    <Setter Property=""Padding"" Value=""10,5,10,5"" />
                    <Setter Property=""Cursor"" Value=""Hand"" />
                    <Setter Property=""Template"">
                        <Setter.Value>
                            <ControlTemplate TargetType=""MenuItem"">
                                <Border Background=""{TemplateBinding Background}"" Padding=""{TemplateBinding Padding}"" CornerRadius=""2"">
                                    <Grid>
                                        <ContentPresenter ContentSource=""Header"" RecognizesAccessKey=""True""/>
                                        <Path x:Name=""Arrow"" Data=""M 0 0 L 4 4 L 0 8 Z"" HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Margin=""10,0,0,0"" Fill=""#D1AB75"" Visibility=""Collapsed"" />
                                    </Grid>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property=""IsHighlighted"" Value=""True"">
                                        <Setter Property=""Background"" Value=""#453B2E"" />
                                        <Setter Property=""Foreground"" Value=""#00DC78"" />
                                    </Trigger>
                                    <Trigger Property=""HasItems"" Value=""True"">
                                        <Setter TargetName=""Arrow"" Property=""Visibility"" Value=""Visible""/>
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style TargetType=""Separator"">
                    <Setter Property=""Background"" Value=""#786450"" />
                    <Setter Property=""Height"" Value=""1"" />
                    <Setter Property=""Margin"" Value=""0,4,0,4"" />
                    <Setter Property=""Template"">
                        <Setter.Value>
                            <ControlTemplate TargetType=""Separator"">
                                <Border Background=""{TemplateBinding Background}"" Height=""{TemplateBinding Height}"" Margin=""{TemplateBinding Margin}"" />
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </ResourceDictionary>";

            try
            {
                var menuResources = (ResourceDictionary)XamlReader.Parse(menuStylesXaml);
                Application.Current.Resources.MergedDictionaries.Add(menuResources);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Menu XAML Error: " + ex.Message);
            }

            string comboBoxItemXaml = @"
            <Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" TargetType=""ComboBoxItem"">
                <Setter Property=""Background"" Value=""Transparent"" />
                <Setter Property=""Foreground"" Value=""#D1AB75"" />
                <Setter Property=""Padding"" Value=""5,2,5,2"" />
                <Setter Property=""Cursor"" Value=""Hand"" />
                <Setter Property=""Template"">
                    <Setter.Value>
                        <ControlTemplate TargetType=""ComboBoxItem"">
                            <Border Background=""{TemplateBinding Background}"" Padding=""{TemplateBinding Padding}"">
                                <ContentPresenter />
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property=""IsHighlighted"" Value=""True"">
                                    <Setter Property=""Background"" Value=""#453B2E"" />
                                    <Setter Property=""Foreground"" Value=""#00DC78"" />
                                </Trigger>
                                <Trigger Property=""IsSelected"" Value=""True"">
                                    <Setter Property=""Foreground"" Value=""#00DC78"" />
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>";

            string comboBoxXaml = @"
            <Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" TargetType=""ComboBox"">
                <Setter Property=""Foreground"" Value=""#D1AB75"" />
                <Setter Property=""Background"" Value=""#332D23"" />
                <Setter Property=""BorderBrush"" Value=""#786450"" />
                <Setter Property=""Cursor"" Value=""Hand"" />
                <Setter Property=""Template"">
                    <Setter.Value>
                        <ControlTemplate TargetType=""ComboBox"">
                            <Grid>
                                <ToggleButton Name=""ToggleButton"" Focusable=""false"" ClickMode=""Press"" IsChecked=""{Binding Path=IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"">
                                    <ToggleButton.Template>
                                        <ControlTemplate TargetType=""ToggleButton"">
                                            <Border Background=""#332D23"" BorderBrush=""#786450"" BorderThickness=""1"" CornerRadius=""2"">
                                                <Grid>
                                                    <ContentPresenter />
                                                    <Path HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Margin=""0,0,5,0"" Fill=""#D1AB75"" Data=""M 0 0 L 8 0 L 4 4 Z"" />
                                                </Grid>
                                            </Border>
                                        </ControlTemplate>
                                    </ToggleButton.Template>
                                </ToggleButton>
                                <ContentPresenter Name=""ContentSite"" IsHitTestVisible=""False"" Content=""{TemplateBinding SelectionBoxItem}"" ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}"" ContentTemplateSelector=""{TemplateBinding ItemTemplateSelector}"" Margin=""5,0,18,0"" VerticalAlignment=""Center"" HorizontalAlignment=""Left"" />
                                <Popup Name=""Popup"" Placement=""Bottom"" IsOpen=""{TemplateBinding IsDropDownOpen}"" AllowsTransparency=""True"" Focusable=""False"" PopupAnimation=""Slide"">
                                    <Grid Name=""DropDown"" SnapsToDevicePixels=""True"" MinWidth=""{TemplateBinding ActualWidth}"" MaxHeight=""{TemplateBinding MaxDropDownHeight}"">
                                        <Border Name=""DropDownBorder"" Background=""#332D23"" BorderThickness=""1"" BorderBrush=""#786450"" />
                                        <ScrollViewer Margin=""4,6,4,6"" SnapsToDevicePixels=""True"" HorizontalScrollBarVisibility=""Auto"" VerticalScrollBarVisibility=""Auto"">
                                            <StackPanel IsItemsHost=""True"" KeyboardNavigation.DirectionalNavigation=""Contained"" />
                                        </ScrollViewer>
                                    </Grid>
                                </Popup>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>";

            try
            {
                Application.Current.Resources[typeof(ComboBoxItem)] = (Style)XamlReader.Parse(comboBoxItemXaml);
                Application.Current.Resources[typeof(ComboBox)] = (Style)XamlReader.Parse(comboBoxXaml);
            }
            catch (Exception ex)
            {
                MessageBox.Show("ComboBox XAML Error: " + ex.Message);
            }

            string scrollBarStyleXaml = @"
            <Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" TargetType=""ScrollBar"">
                <Style.Resources>
                    <Style TargetType=""RepeatButton"">
                        <Setter Property=""Background"" Value=""Transparent"" />
                        <Setter Property=""BorderBrush"" Value=""Transparent"" />
                        <Setter Property=""Template"">
                            <Setter.Value>
                                <ControlTemplate TargetType=""RepeatButton"">
                                    <Border Background=""Transparent"" />
                                </ControlTemplate>
                            </Setter.Value>
                        </Setter>
                    </Style>
                    <Style TargetType=""Thumb"">
                        <Setter Property=""Background"" Value=""#786450"" />
                        <Setter Property=""BorderBrush"" Value=""Transparent"" />
                        <Setter Property=""BorderThickness"" Value=""0"" />
                        <Setter Property=""Margin"" Value=""2"" />
                        <Setter Property=""Template"">
                            <Setter.Value>
                                <ControlTemplate TargetType=""Thumb"">
                                    <Border Background=""{TemplateBinding Background}"" CornerRadius=""4"" />
                                </ControlTemplate>
                            </Setter.Value>
                        </Setter>
                    </Style>
                </Style.Resources>
                <Setter Property=""Background"" Value=""#332D23"" />
                <Setter Property=""BorderBrush"" Value=""#332D23"" />
                <Setter Property=""BorderThickness"" Value=""0"" />
                <Setter Property=""Template"">
                    <Setter.Value>
                        <ControlTemplate TargetType=""ScrollBar"">
                            <Grid Background=""{TemplateBinding Background}"">
                                <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
                                    <Track.DecreaseRepeatButton>
                                        <RepeatButton Command=""ScrollBar.PageUpCommand"" />
                                    </Track.DecreaseRepeatButton>
                                    <Track.Thumb>
                                        <Thumb />
                                    </Track.Thumb>
                                    <Track.IncreaseRepeatButton>
                                        <RepeatButton Command=""ScrollBar.PageDownCommand"" />
                                    </Track.IncreaseRepeatButton>
                                </Track>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
                <Style.Triggers>
                    <Trigger Property=""Orientation"" Value=""Horizontal"">
                        <Setter Property=""Width"" Value=""Auto"" />
                        <Setter Property=""Height"" Value=""8"" />
                        <Setter Property=""MinHeight"" Value=""8"" />
                    </Trigger>
                    <Trigger Property=""Orientation"" Value=""Vertical"">
                        <Setter Property=""Width"" Value=""8"" />
                        <Setter Property=""MinWidth"" Value=""8"" />
                        <Setter Property=""Height"" Value=""Auto"" />
                    </Trigger>
                </Style.Triggers>
            </Style>";

            try
            {
                var scrollStyle = (Style)XamlReader.Parse(scrollBarStyleXaml);
                Application.Current.Resources[typeof(ScrollBar)] = scrollStyle;
                this.Resources[typeof(ScrollBar)] = scrollStyle;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Scrollbar XAML Error: " + ex.Message);
            }

            Title = "TexBowser";
            Width = 1100;
            Height = 760;
            Background = new SolidColorBrush(BgColor);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _rootGrid = new Grid();
            Content = _rootGrid;
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var inputPanel = new DockPanel { LastChildFill = true, Background = new SolidColorBrush(PanelColor) };
            Grid.SetRow(inputPanel, 0);
            _rootGrid.Children.Add(inputPanel);

            _modeComboBox = new ComboBox
            {
                Width = 90,
                Height = 24,
                Margin = new Thickness(5, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = AppFont,
                FontSize = 12
            };
            _modeComboBox.Items.Add("web");
            _modeComboBox.Items.Add("summary");
            _modeComboBox.SelectedIndex = 0;
            _modeComboBox.SelectionChanged += (s, e) =>
            {
                if (_activeTab != null && _modeComboBox.SelectedItem != null)
                {
                    _activeTab.Mode = _modeComboBox.SelectedItem.ToString();
                }
            };
            DockPanel.SetDock(_modeComboBox, Dock.Right);
            inputPanel.Children.Add(_modeComboBox);

            _bookmarkMenu = new ContextMenu();
            _btnBookmarks = CreateFlatButton("★", TextColor, OutlineColor, 14, new Thickness(8, 0, 8, 0), new Thickness(0, 4, 4, 4));
            _btnBookmarks.Click += (s, e) =>
            {
                UpdateBookmarkMenu();
                _bookmarkMenu.PlacementTarget = _btnBookmarks;
                _bookmarkMenu.Placement = PlacementMode.Bottom;
                _bookmarkMenu.IsOpen = true;
            };
            DockPanel.SetDock(_btnBookmarks, Dock.Right);
            inputPanel.Children.Add(_btnBookmarks);

            _spinnerBlock = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(AccentColor),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                FontFamily = AppFont,
                FontSize = 16,
                Padding = new Thickness(0, 0, 15, 0),
                Width = 25,
                Visibility = Visibility.Collapsed
            };
            DockPanel.SetDock(_spinnerBlock, Dock.Right);
            inputPanel.Children.Add(_spinnerBlock);

            var navPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            DockPanel.SetDock(navPanel, Dock.Left);
            inputPanel.Children.Add(navPanel);

            _btnPrev = CreateFlatButton("←", TextColor, OutlineColor, 16, new Thickness(8, 0, 8, 0), new Thickness(0, 4, 4, 4));
            _btnPrev.Click += BtnPrev_Click;
            navPanel.Children.Add(_btnPrev);

            _btnNext = CreateFlatButton("→", TextColor, OutlineColor, 16, new Thickness(8, 0, 8, 0), new Thickness(0, 4, 4, 4));
            _btnNext.Click += BtnNext_Click;
            navPanel.Children.Add(_btnNext);

            _btnStop = CreateFlatButton("■", Colors.Red, OutlineColor, 12, new Thickness(8, 0, 8, 0), new Thickness(0, 4, 8, 4));
            _btnStop.Click += BtnStop_Click;
            navPanel.Children.Add(_btnStop);

            var promptLabel = new TextBlock
            {
                Text = " > ",
                Foreground = new SolidColorBrush(AccentColor),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                FontFamily = AppFont,
                FontSize = 14
            };
            DockPanel.SetDock(promptLabel, Dock.Left);
            inputPanel.Children.Add(promptLabel);

            _inputBox = new TextBox
            {
                Background = new SolidColorBrush(PanelColor),
                Foreground = new SolidColorBrush(TextColor),
                BorderThickness = new Thickness(0),
                FontFamily = AppFont,
                FontSize = 14,
                Padding = new Thickness(2, 8, 4, 8),
                CaretBrush = new SolidColorBrush(AccentColor),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _inputBox.KeyDown += OnInputKeyDown;
            inputPanel.Children.Add(_inputBox);

            var tabBar = new DockPanel { Background = new SolidColorBrush(BgColor), LastChildFill = true };
            Grid.SetRow(tabBar, 1);
            _rootGrid.Children.Add(tabBar);

            _btnAddTab = new Button
            {
                Content = "+",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(OutlineColor),
                Foreground = new SolidColorBrush(TextColor),
                FontFamily = AppFont,
                FontSize = 14,
                Width = 28,
                Height = 24,
                Margin = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ApplyFlatTemplate(_btnAddTab);
            _btnAddTab.Click += (s, e) => AddNewTab("");
            DockPanel.SetDock(_btnAddTab, Dock.Left);
            tabBar.Children.Add(_btnAddTab);

            var tabScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _tabPanel = new StackPanel { Orientation = Orientation.Horizontal };
            tabScroll.Content = _tabPanel;
            tabBar.Children.Add(tabScroll);

            _outputGrid = new Grid();
            Grid.SetRow(_outputGrid, 2);
            _rootGrid.Children.Add(_outputGrid);

            _outputBox = new RichTextBox
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                Background = new SolidColorBrush(BgColor),
                Foreground = new SolidColorBrush(TextColor),
                BorderThickness = new Thickness(0),
                FontFamily = AppFont,
                FontSize = 14,
                Padding = new Thickness(10, 6, 10, 6),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CaretBrush = new SolidColorBrush(AccentColor)
            };

            try
            {
                var scrollStyle2 = (Style)XamlReader.Parse(scrollBarStyleXaml);
                _outputBox.Resources[typeof(ScrollBar)] = scrollStyle2;
            }
            catch { }
            _outputGrid.Children.Add(_outputBox);

            _outputBox.PreviewMouseLeftButtonDown += OutputBox_PreviewMouseLeftButtonDown;

            _outputContextMenu = new ContextMenu();

            _outputBox.PreviewMouseRightButtonUp += (sender, e) =>
            {
                e.Handled = true;
                _outputContextMenu.Items.Clear();

                var rightClickedLink = GetHyperlinkAt(e.GetPosition(_outputBox));
                string rightClickedHref = GetHyperlinkHref(rightClickedLink);
                if (rightClickedLink != null && !string.IsNullOrEmpty(rightClickedHref))
                {
                    string linkUrl = ResolveUrl(rightClickedHref, _activeTab.CurrentUrl);

                    var openNewTabItem = new MenuItem { Header = "Open in New Tab" };
                    openNewTabItem.Click += (s, ev) => AddNewTab(linkUrl);
                    _outputContextMenu.Items.Add(openNewTabItem);

                    var saveLinkItem = new MenuItem { Header = "Save Link As..." };
                    saveLinkItem.Click += (s, ev) => _ = SaveLinkAsAsync(linkUrl);
                    _outputContextMenu.Items.Add(saveLinkItem);

                    var copyLinkItem = new MenuItem { Header = "Copy Link" };
                    copyLinkItem.Click += (s, ev) =>
                    {
                        try { Clipboard.SetText(linkUrl); } catch { }
                    };
                    _outputContextMenu.Items.Add(copyLinkItem);

                    _outputContextMenu.PlacementTarget = _outputBox;
                    _outputContextMenu.IsOpen = true;
                    return;
                }

                var copyItem = new MenuItem { Header = "Copy" };
                copyItem.Click += (s, ev) => _outputBox.Copy();
                var selectAllItem = new MenuItem { Header = "Select All" };
                selectAllItem.Click += (s, ev) => _outputBox.SelectAll();

                _outputContextMenu.Items.Add(copyItem);
                _outputContextMenu.Items.Add(selectAllItem);
                _outputContextMenu.Items.Add(new Separator());

                var bookmarkItem = new MenuItem { Header = "Bookmark This Page" };
                bookmarkItem.Click += AddBookmark;
                _outputContextMenu.Items.Add(bookmarkItem);

                if (_settings.Bookmarks.Count > 0)
                {
                    var bookmarksParent = new MenuItem { Header = "Bookmarks" };
                    foreach (var url in _settings.Bookmarks)
                    {
                        var item = new MenuItem { Header = url };
                        string capturedUrl = url;
                        item.Click += (s, ev) => _ = ProcessInputAsync(capturedUrl);
                        bookmarksParent.Items.Add(item);
                    }
                    bookmarksParent.Items.Add(new Separator());
                    var clearItem1 = new MenuItem { Header = "Clear Bookmarks", Foreground = new SolidColorBrush(Colors.Red) };
                    clearItem1.Click += (s, ev) => { _settings.Bookmarks.Clear(); SaveSettings(); };
                    bookmarksParent.Items.Add(clearItem1);

                    _outputContextMenu.Items.Add(bookmarksParent);
                }

                var sourceItem = new MenuItem { Header = "View Page Source" };
                sourceItem.Click += ViewSource;
                var clearItem = new MenuItem { Header = "Clear Page" };
                clearItem.Click += (s, ev) => { _activeTab.ConsoleBuffer.Clear(); RenderConsole(); };

                _outputContextMenu.Items.Add(sourceItem);
                _outputContextMenu.Items.Add(new Separator());
                _outputContextMenu.Items.Add(clearItem);

                _outputContextMenu.PlacementTarget = _outputBox;
                _outputContextMenu.IsOpen = true;
            };

            _searchPanel = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 20, 20),
                Padding = new Thickness(5),
                Background = new SolidColorBrush(PanelColor),
                BorderBrush = new SolidColorBrush(OutlineColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Visibility = Visibility.Collapsed
            };
            _outputGrid.Children.Add(_searchPanel);

            var searchDock = new DockPanel { LastChildFill = true, Margin = new Thickness(0) };
            _searchPanel.Child = searchDock;

            _btnSearchClose = CreateFlatButton("x", TextColor, OutlineColor, 10, new Thickness(2), new Thickness(5, 0, 0, 0));
            _btnSearchClose.Click += (s, e) => { _searchPanel.Visibility = Visibility.Collapsed; ClearHighlights(); };
            DockPanel.SetDock(_btnSearchClose, Dock.Right);
            searchDock.Children.Add(_btnSearchClose);

            _searchBox = new TextBox
            {
                Width = 200,
                Background = new SolidColorBrush(BgColor),
                Foreground = new SolidColorBrush(TextColor),
                BorderThickness = new Thickness(0),
                FontFamily = AppFont,
                FontSize = 12,
                Padding = new Thickness(5, 2, 5, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = new SolidColorBrush(AccentColor)
            };
            _searchBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    HighlightText(_searchBox.Text);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    _searchPanel.Visibility = Visibility.Collapsed;
                    ClearHighlights();
                }
            };
            searchDock.Children.Add(_searchBox);

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    _searchPanel.Visibility = Visibility.Visible;
                    _searchBox.Focus();
                    _searchBox.SelectAll();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    if (_searchPanel.Visibility == Visibility.Visible)
                    {
                        _searchPanel.Visibility = Visibility.Collapsed;
                        ClearHighlights();
                        e.Handled = true;
                    }
                }
            };

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _activeTab != null && _activeTab.IsProcessing && _activeTab.Cts != null)
                {
                    _activeTab.Cts.Cancel();
                }
            };

            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TexBowser");
            Directory.CreateDirectory(appDir);
            _settingsPath = Path.Combine(appDir, "config.json");

            try
            {
                if (!LoadSettings())
                {
                    if (File.Exists(_settingsPath))
                    {
                        var result = MessageBox.Show("TexBowser's configuration file is corrupted or invalid.\n\nReset to default settings and restart?", "Configuration Error", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (result == MessageBoxResult.Yes)
                        {
                            try { File.Delete(_settingsPath); } catch { }
                            Process.Start(Environment.ProcessPath);
                            Application.Current.Shutdown();
                            return;
                        }
                        else
                        {
                            Application.Current.Shutdown();
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fatal initialization error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            AddNewTab("");
            _activeTab.ConsoleBuffer.AppendLine("enter web address...");
            RenderConsole();

            if (!string.IsNullOrEmpty(_settings.ModelPath) && File.Exists(_settings.ModelPath))
            {
                _ = LoadModelAsync(_settings.ModelPath);
            }

            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _inputBox.Focus()));
        }

        private Hyperlink GetHyperlinkAt(Point p)
        {
            TextPointer tp = null;
            try { tp = _outputBox.GetPositionFromPoint(p, true); }
            catch { }

            if (tp == null) return null;

            DependencyObject element = tp.Parent;
            while (element != null)
            {
                if (element is Hyperlink hl) return hl;
                if (element is TextElement te)
                    element = te.Parent;
                else
                    element = LogicalTreeHelper.GetParent(element);
            }

            if (tp.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart)
            {
                var elem = tp.GetAdjacentElement(LogicalDirection.Forward);
                if (elem is Hyperlink hlFwd) return hlFwd;
            }
            if (tp.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.ElementStart)
            {
                var elem = tp.GetAdjacentElement(LogicalDirection.Backward);
                if (elem is Hyperlink hlBwd) return hlBwd;
            }

            return null;
        }

        // Walks up from the element that actually received the mouse event
        // (e.OriginalSource). For clicks on text inside a RichTextBox/
        // FlowDocument, OriginalSource is very often the Run/Hyperlink
        // content element itself, which is far more reliable than
        // reconstructing the element from pixel coordinates.
        private Hyperlink FindHyperlinkAncestor(DependencyObject source)
        {
            DependencyObject element = source;
            while (element != null)
            {
                if (element is Hyperlink hl) return hl;
                if (element is TextElement te)
                    element = te.Parent;
                else if (element is FrameworkContentElement fce)
                    element = fce.Parent;
                else if (element is FrameworkElement fe)
                    element = fe.Parent ?? LogicalTreeHelper.GetParent(element);
                else
                    element = LogicalTreeHelper.GetParent(element);
            }
            return null;
        }

        private void Hyperlink_Click(object sender, RoutedEventArgs e)
        {
            NavigateHyperlink(sender as Hyperlink);
        }

        // MdXaml's plain Markdown.Transform() does NOT set NavigateUri; it
        // stores the href in CommandParameter instead. Check both.
        private static string GetHyperlinkHref(Hyperlink link)
        {
            if (link == null) return null;
            string href = link.NavigateUri?.ToString();
            if (string.IsNullOrEmpty(href))
                href = link.CommandParameter as string;
            return href;
        }

        private void NavigateHyperlink(Hyperlink link)
        {
            if (link == null) return;
            if (_activeTab == null) return;

            string rawHref = GetHyperlinkHref(link);
            if (string.IsNullOrEmpty(rawHref)) return;

            string absUrl = ResolveUrl(rawHref, _activeTab.CurrentUrl);

            if (_activeTab.IsProcessing)
            {
                PrintError("[Debug] Tab is already processing. Ignoring click.");
                return;
            }

            PrintInfo($"Navigating to: {absUrl}");
            _ = ProcessInputAsync(absUrl);
        }

        private async Task SaveLinkAsAsync(string url)
        {
            string suggestedName;
            try
            {
                var uri = new Uri(url);
                suggestedName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(suggestedName)) suggestedName = "download";
            }
            catch { suggestedName = "download"; }

            var dlg = new SaveFileDialog
            {
                FileName = suggestedName,
                Filter = "All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            string savePath = dlg.FileName;
            try
            {
                PrintInfo($"Downloading {url} ...");
                byte[] bytes = await _httpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(savePath, bytes);
                PrintSuccess($"Saved to {savePath}");
            }
            catch (Exception ex)
            {
                PrintError($"Save failed: {ex.Message}");
            }
        }

        private void OutputBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var hl = FindHyperlinkAncestor(e.OriginalSource as DependencyObject);

            if (hl == null)
            {
                var pt = e.GetPosition(_outputBox);
                hl = GetHyperlinkAt(pt);
            }

            Debug.WriteLine(hl != null
                ? $"[link-click] OriginalSource={e.OriginalSource?.GetType().Name}, hit hyperlink -> {GetHyperlinkHref(hl)}"
                : $"[link-click] OriginalSource={e.OriginalSource?.GetType().Name}, no hyperlink found at click point");

            if (hl != null)
            {
                e.Handled = true;
                NavigateHyperlink(hl);
            }
        }

        private void ApplyFlatTemplate(Button btn)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.BorderBrushProperty, btn.BorderBrush);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            template.VisualTree = border;
            btn.Template = template;
        }

        private Button CreateFlatButton(string content, Color foreColor, Color borderColor, double fontSize, Thickness padding, Thickness margin)
        {
            var btn = new Button
            {
                Content = content,
                Foreground = new SolidColorBrush(foreColor),
                FontFamily = AppFont,
                FontSize = fontSize,
                Padding = padding,
                Margin = margin,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(borderColor),
                Cursor = Cursors.Hand,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 24
            };
            ApplyFlatTemplate(btn);
            return btn;
        }

        private void ClearHighlights()
        {
            foreach (var range in _highlightedRanges)
            {
                range.ApplyPropertyValue(TextElement.BackgroundProperty, null);
                range.ApplyPropertyValue(TextElement.ForegroundProperty, null);
            }
            _highlightedRanges.Clear();
        }

        private void HighlightText(string query)
        {
            ClearHighlights();
            if (string.IsNullOrWhiteSpace(query)) return;

            var doc = _outputBox.Document;
            TextPointer pointer = doc.ContentStart;

            while (pointer != null)
            {
                string textInRun = pointer.GetTextInRun(LogicalDirection.Forward);
                if (!string.IsNullOrEmpty(textInRun))
                {
                    int index = 0;
                    while (index < textInRun.Length)
                    {
                        int foundIndex = textInRun.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
                        if (foundIndex >= 0)
                        {
                            TextPointer start = pointer.GetPositionAtOffset(foundIndex);
                            TextPointer end = start.GetPositionAtOffset(query.Length);
                            if (start != null && end != null)
                            {
                                TextRange range = new TextRange(start, end);
                                range.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Colors.Yellow));
                                range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Colors.Black));
                                _highlightedRanges.Add(range);
                                index = foundIndex + query.Length;
                            }
                            else
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
        }

        private void UpdateBookmarkMenu()
        {
            _bookmarkMenu.Items.Clear();

            var addItem = new MenuItem { Header = "Bookmark This Page" };
            addItem.Click += AddBookmark;
            _bookmarkMenu.Items.Add(addItem);

            if (_settings.Bookmarks.Count > 0)
            {
                _bookmarkMenu.Items.Add(new Separator());
                foreach (var url in _settings.Bookmarks)
                {
                    var item = new MenuItem { Header = url };
                    string capturedUrl = url;
                    item.Click += (s, e) => _ = ProcessInputAsync(capturedUrl);
                    _bookmarkMenu.Items.Add(item);
                }
                _bookmarkMenu.Items.Add(new Separator());
                var clearItem = new MenuItem { Header = "Clear Bookmarks", Foreground = new SolidColorBrush(Colors.Red) };
                clearItem.Click += (s, e) => { _settings.Bookmarks.Clear(); SaveSettings(); };
                _bookmarkMenu.Items.Add(clearItem);
            }
        }

        private void AddBookmark(object sender, RoutedEventArgs e)
        {
            if (_activeTab != null && !string.IsNullOrEmpty(_activeTab.CurrentUrl) && !_settings.Bookmarks.Contains(_activeTab.CurrentUrl))
            {
                _settings.Bookmarks.Add(_activeTab.CurrentUrl);
                SaveSettings();
            }
        }

        private void ViewSource(object sender, RoutedEventArgs e)
        {
            if (_activeTab == null || string.IsNullOrEmpty(_activeTab.RawHtml)) return;
            _activeTab.ConsoleBuffer.Clear();
            _activeTab.ConsoleBuffer.AppendLine("```html");
            _activeTab.ConsoleBuffer.AppendLine(_activeTab.RawHtml);
            _activeTab.ConsoleBuffer.AppendLine("```");
            RenderConsole();
        }

        private void AddNewTab(string url)
        {
            var tab = new BrowserTab();
            _tabs.Add(tab);

            var tabUI = new Border
            {
                BorderBrush = new SolidColorBrush(OutlineColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(8, 2, 4, 2),
                Margin = new Thickness(0, 2, 4, 2),
                Background = new SolidColorBrush(PanelColor),
                Cursor = Cursors.Hand,
                Tag = tab,
                Width = 160
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = tab.Title,
                Foreground = new SolidColorBrush(TextColor),
                FontFamily = AppFont,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            var closeBtn = new Button
            {
                Content = "x",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(TextColor),
                FontFamily = AppFont,
                FontSize = 10,
                Width = 16,
                Height = 16,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Tag = tab
            };
            ApplyFlatTemplate(closeBtn);
            closeBtn.Click += CloseTab_Click;
            Grid.SetColumn(closeBtn, 1);
            grid.Children.Add(closeBtn);

            tabUI.Child = grid;
            tabUI.MouseLeftButtonUp += (s, e) => SwitchTab(tab);

            var menu = new ContextMenu();
            var closeItem = new MenuItem { Header = "Close", Tag = tab };
            closeItem.Click += CloseTab_Click;
            menu.Items.Add(closeItem);
            tabUI.ContextMenu = menu;

            _tabPanel.Children.Add(tabUI);

            tab.TabUI = tabUI;
            tab.TabTitle = title;

            SwitchTab(tab);

            if (!string.IsNullOrEmpty(url))
            {
                _ = ProcessInputAsync(url);
            }
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            BrowserTab tabToClose = null;
            if (sender is Button btn && btn.Tag is BrowserTab t) tabToClose = t;
            else if (sender is MenuItem mi && mi.Tag is BrowserTab mt) tabToClose = mt;

            if (tabToClose != null) CloseTab(tabToClose);
        }

        private void CloseTab(BrowserTab tab)
        {
            if (_tabs.Count == 1) return;

            int index = _tabs.IndexOf(tab);
            _tabs.Remove(tab);

            if (_activeTab == tab)
            {
                int newIndex = Math.Min(index, _tabs.Count - 1);
                SwitchTab(_tabs[newIndex]);
            }
            _tabPanel.Children.Remove(tab.TabUI);
        }

        private void SwitchTab(BrowserTab tab)
        {
            _activeTab = tab;
            UpdateTabStyles();
            UpdateNavButtons();

            _inputBox.Text = tab.CurrentUrl;
            _inputBox.Select(_inputBox.Text.Length, 0);

            if (_modeComboBox.SelectedItem?.ToString() != tab.Mode)
            {
                _modeComboBox.SelectedItem = tab.Mode;
            }

            RenderConsole();
        }

        private void UpdateTabStyles()
        {
            foreach (var tab in _tabs)
            {
                if (tab == _activeTab)
                {
                    tab.TabUI.BorderBrush = new SolidColorBrush(AccentColor);
                    tab.TabUI.Background = new SolidColorBrush(BgColor);
                }
                else
                {
                    tab.TabUI.BorderBrush = new SolidColorBrush(OutlineColor);
                    tab.TabUI.Background = new SolidColorBrush(PanelColor);
                }
            }
        }

        private void UpdateNavButtons()
        {
            if (_activeTab == null) return;
            _btnPrev.IsEnabled = _activeTab.History.Count > 0;
            _btnNext.IsEnabled = _activeTab.ForwardHistory.Count > 0;
            _btnStop.IsEnabled = _activeTab.IsProcessing;
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab.IsProcessing || _activeTab.History.Count == 0) return;
            if (_activeTab.CurrentPage != null) _activeTab.ForwardHistory.Push(_activeTab.CurrentPage);
            _activeTab.CurrentPage = _activeTab.History.Pop();
            RenderHistoryPage(_activeTab);
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab.IsProcessing || _activeTab.ForwardHistory.Count == 0) return;
            if (_activeTab.CurrentPage != null) _activeTab.History.Push(_activeTab.CurrentPage);
            _activeTab.CurrentPage = _activeTab.ForwardHistory.Pop();
            RenderHistoryPage(_activeTab);
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab.IsProcessing && _activeTab.Cts != null)
            {
                _activeTab.Cts.Cancel();
            }
        }

        private bool LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    _settings = new AppSettings();
                }

                if (_settings.ContextSize == 0 || _settings.ContextSize > 1000000) _settings.ContextSize = 100000;
                if (_settings.MaxHtmlChars < 1000) _settings.MaxHtmlChars = 50000;
                if (_settings.MaxLLMOutputTokens < 100) _settings.MaxLLMOutputTokens = 8192;
                if (_settings.GpuLayerCount < 0) _settings.GpuLayerCount = 0;
                if (_settings.MainGpuId < 0) _settings.MainGpuId = 0;

                if (_settings.Bookmarks == null) _settings.Bookmarks = new List<string>();

                if (_settings.Temperature < 0) _settings.Temperature = 0.8f;
                if (_settings.TopK < 0) _settings.TopK = 40;
                if (_settings.TopP < 0 || _settings.TopP > 1) _settings.TopP = 0.9f;
                if (_settings.RepeatPenalty < 1.0f) _settings.RepeatPenalty = 1.1f;

                return true;
            }
            catch
            {
                _settings = new AppSettings();
                return false;
            }
        }

        private void SaveSettings()
        {
            try
            {
                if (_settings.Bookmarks == null) _settings.Bookmarks = new List<string>();
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        private async Task LoadModelAsync(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;
            modelPath = modelPath.Trim().Trim('"');

            if (_modelLoaded)
            {
                _executor = null;
                _model?.Dispose();
                _model = null;
                _modelLoaded = false;
            }

            try
            {
                if (!File.Exists(modelPath)) return;
                _settings.ModelPath = modelPath;
                SaveSettings();

                await Task.Run(() =>
                {
                    _modelParams = new ModelParams(modelPath)
                    {
                        ContextSize = _settings.ContextSize,
                        GpuLayerCount = _settings.GpuLayerCount,
                        MainGpu = _settings.MainGpuId,
                        FlashAttention = _settings.UseFlashAttention,
                        UseMemorymap = _settings.UseMemorymap
                    };
                    _model = LLamaWeights.LoadFromFile(_modelParams);
                    _executor = new StatelessExecutor(_model, _modelParams);
                });

                _modelLoaded = true;
                PrintSuccess($"Model loaded successfully!");
            }
            catch (Exception ex)
            {
                _modelLoaded = false;
                PrintError($"Failed to load model: {ex.Message}");
                if (ex.InnerException != null)
                {
                    PrintError($"Details: {ex.InnerException.Message}");
                }
                PrintError("If using MiniCPM, try 'set-flash:false' and 'set-context:4096'.");
            }
        }

        private void ShowHelp()
        {
            // Build the whole block and append once — calling PrintLine per
            // line here used to trigger a full FlowDocument rebuild
            // (markdown reparse + render) for every single line.
            var sb = new StringBuilder();
            sb.AppendLine("");
            sb.AppendLine("`[i] == Available commands ==`");
            sb.AppendLine("  load-model:<path>  Load AI model (saves path for next boot)");
            sb.AppendLine("  reload-model       Reload current model (applies new settings)");
            sb.AppendLine("  set-context:<n>    Set max context size (default: 100000)");
            sb.AppendLine("  set-gpu:<n>        Set GPU layers (0 for CPU only)");
            sb.AppendLine("  set-gpuid:<n>      Set which GPU to use (default: 0)");
            sb.AppendLine("  set-flash:<bool>   Enable/Disable Flash Attention (true/false)");
            sb.AppendLine("  set-mmap:<bool>    Enable/Disable Memory Mapping (true/false)");
            sb.AppendLine("  set-htmlmax:<n>    Set max HTML chars to send to LLM (default 50000)");
            sb.AppendLine("  set-llmmax:<n>     Set max LLM output tokens (default 8192)");
            sb.AppendLine("  --- Sampling ---");
            sb.AppendLine("  set-temp:<val>     Set Temperature (default: 0.8)");
            sb.AppendLine("  set-topk:<val>     Set Top-K (default: 40)");
            sb.AppendLine("  set-topp:<val>     Set Top-P (default: 0.9)");
            sb.AppendLine("  set-reppen:<val>   Set Repeat Penalty (default: 1.1)");
            sb.AppendLine("  -----------------");
            sb.AppendLine("  <url>              Navigate to URL (e.g., https://example.com)");
            sb.AppendLine("  <number>           Follow link # from current page");
            sb.AppendLine("  links              List links on current page");
            sb.AppendLine("  back               Go to previous page");
            sb.AppendLine("  clear              Clear screen");
            sb.AppendLine("  exit               Quit application");
            sb.Append("");
            AppendOutput(sb.ToString());
        }

        private void ShowLinks()
        {
            if (_activeTab.Links.Count == 0) { PrintInfo("No links on current page."); return; }
            var sb = new StringBuilder();
            sb.AppendLine("");
            sb.AppendLine($"`[i] == Links on current page ({_activeTab.Links.Count}) ==`");
            for (int i = 0; i < _activeTab.Links.Count; i++)
            {
                var (text, url) = _activeTab.Links[i];
                if (text.Length > 50) text = text.Substring(0, 47) + "...";
                sb.AppendLine($"  [{i + 1,2}] {text}");
                sb.AppendLine($"        -> {url}");
            }
            sb.Append("");
            AppendOutput(sb.ToString());
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var input = _inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input)) return;

            var lowerInput = input.ToLowerInvariant();

            bool isCommand = lowerInput == "exit" || lowerInput == "quit" || lowerInput == "help" || lowerInput == "?" ||
                             lowerInput == "clear" || lowerInput == "cls" || lowerInput == "back" || lowerInput == "links" ||
                             lowerInput == "reload-model" || lowerInput.StartsWith("set-") || lowerInput.StartsWith("load-model:") ||
                             (int.TryParse(input, out _));

            if (isCommand)
            {
                _inputBox.Clear();
            }
            else
            {
                string targetUrl = input;
                if (!targetUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    targetUrl = "https://" + targetUrl;

                _inputBox.Text = targetUrl;
                _inputBox.Select(_inputBox.Text.Length, 0);
            }

            _ = ProcessInputAsync(input);
        }

        private async Task ProcessInputAsync(string input)
        {
            if (_activeTab.IsProcessing) return;

            _activeTab.IsProcessing = true;
            UpdateNavButtons();
            _activeTab.Cts = new CancellationTokenSource();
            try
            {
                var lowerInput = input.ToLowerInvariant();

                if (lowerInput == "exit" || lowerInput == "quit") { Application.Current.Shutdown(); return; }
                if (lowerInput == "help" || lowerInput == "?") { ShowHelp(); return; }
                if (lowerInput == "clear" || lowerInput == "cls")
                {
                    _activeTab.ConsoleBuffer.Clear();
                    _activeTab.ConsoleBuffer.AppendLine("enter web address...");
                    RenderConsole();
                    return;
                }

                if (lowerInput.StartsWith("set-context:"))
                {
                    if (uint.TryParse(input.Substring("set-context:".Length).Trim(), out uint val))
                    {
                        _settings.ContextSize = val; SaveSettings();
                        PrintSuccess($"Context size set to {val}. Type 'reload-model' to apply.");
                    }
                    return;
                }
                if (lowerInput.StartsWith("set-gpu:"))
                {
                    if (int.TryParse(input.Substring("set-gpu:".Length).Trim(), out int val))
                    {
                        _settings.GpuLayerCount = val; SaveSettings();
                        PrintSuccess($"GPU layers set to {val}. Type 'reload-model' to apply.");
                    }
                    return;
                }
                if (lowerInput.StartsWith("set-gpuid:"))
                {
                    if (int.TryParse(input.Substring("set-gpuid:".Length).Trim(), out int val))
                    {
                        _settings.MainGpuId = val; SaveSettings();
                        PrintSuccess($"Main GPU ID set to {val}. Type 'reload-model' to apply.");
                    }
                    return;
                }
                if (lowerInput.StartsWith("set-flash:"))
                {
                    if (bool.TryParse(input.Substring("set-flash:".Length).Trim(), out bool val))
                    {
                        _settings.UseFlashAttention = val; SaveSettings();
                        PrintSuccess($"Flash Attention set to {val}. Type 'reload-model' to apply.");
                    }
                    else PrintError("Invalid value. Use true or false.");
                    return;
                }
                if (lowerInput.StartsWith("set-mmap:"))
                {
                    if (bool.TryParse(input.Substring("set-mmap:".Length).Trim(), out bool val))
                    {
                        _settings.UseMemorymap = val; SaveSettings();
                        PrintSuccess($"Memory Map set to {val}. Type 'reload-model' to apply.");
                    }
                    else PrintError("Invalid value. Use true or false.");
                    return;
                }
                if (lowerInput.StartsWith("set-htmlmax:"))
                {
                    if (int.TryParse(input.Substring("set-htmlmax:".Length).Trim(), out int val))
                    {
                        _settings.MaxHtmlChars = val; SaveSettings();
                        PrintSuccess($"Max HTML chars set to {val}.");
                    }
                    return;
                }
                if (lowerInput.StartsWith("set-llmmax:"))
                {
                    if (int.TryParse(input.Substring("set-llmmax:".Length).Trim(), out int val))
                    {
                        _settings.MaxLLMOutputTokens = val; SaveSettings();
                        PrintSuccess($"Max LLM output tokens set to {val}.");
                    }
                    return;
                }

                if (lowerInput.StartsWith("set-temp:"))
                {
                    if (float.TryParse(input.Substring("set-temp:".Length).Trim(), out float val))
                    {
                        _settings.Temperature = val; SaveSettings();
                        PrintSuccess($"Temperature set to {val}. (Applies on next generation).");
                    }
                    return;
                }
                if (lowerInput.StartsWith("set-topk:"))
                {
                    if (int.TryParse(input.Substring("set-topk:".Length).Trim(), out int val))
                    {
                        _settings.TopK = val; SaveSettings();
                        PrintSuccess($"Top-K set to {val}. (Applies on next generation).");
                    }
                    return;
                }
                if (lowerInput.StartsWith("set-topp:"))
                {
                    if (float.TryParse(input.Substring("set-topp:".Length).Trim(), out float val))
                    {
                        _settings.TopP = val; SaveSettings();
                        PrintSuccess($"Top-P set to {val}. (Applies on next generation).");
                    }
                    return;
                }
                if (lowerInput.StartsWith("set-reppen:"))
                {
                    if (float.TryParse(input.Substring("set-reppen:".Length).Trim(), out float val))
                    {
                        _settings.RepeatPenalty = val; SaveSettings();
                        PrintSuccess($"Repeat Penalty set to {val}. (Applies on next generation).");
                    }
                    return;
                }

                if (lowerInput.StartsWith("load-model:"))
                {
                    string path = input.Substring("load-model:".Length);
                    await LoadModelAsync(path);
                    return;
                }
                if (lowerInput == "reload-model")
                {
                    if (!string.IsNullOrEmpty(_settings.ModelPath))
                        await LoadModelAsync(_settings.ModelPath);
                    else
                        PrintError("No model path saved. Use load-model:<path> first.");
                    return;
                }

                if (!_modelLoaded)
                {
                    PrintError("No model loaded. Type load-model:<path_to_model.gguf> first.");
                    return;
                }

                if (lowerInput == "back")
                {
                    if (_activeTab.History.Count > 0)
                    {
                        if (_activeTab.CurrentPage != null) _activeTab.ForwardHistory.Push(_activeTab.CurrentPage);
                        _activeTab.CurrentPage = _activeTab.History.Pop();
                        RenderHistoryPage(_activeTab);
                    }
                    else PrintInfo("No history yet.");
                    return;
                }
                if (lowerInput == "links") { ShowLinks(); return; }
                if (int.TryParse(input, out int linkNum) && linkNum > 0 && linkNum <= _activeTab.Links.Count)
                {
                    var url = ResolveUrl(_activeTab.Links[linkNum - 1].url, _activeTab.CurrentUrl);
                    await NavigateAsync(_activeTab, url, true);
                    return;
                }

                string targetUrl = input;
                if (!targetUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    targetUrl = "https://" + targetUrl;
                await NavigateAsync(_activeTab, targetUrl, true);
            }
            catch (OperationCanceledException) { PrintInfo("[cancelled]"); }
            catch (Exception ex) { PrintError($"Error: {ex.Message}"); }
            finally
            {
                _activeTab.IsProcessing = false;
                UpdateNavButtons();
                _inputBox.Focus();
            }
        }

        private async Task NavigateAsync(BrowserTab tab, string url, bool isNewNavigation)
        {
            if (!_modelLoaded)
            {
                if (tab == _activeTab) PrintError("Model not loaded. Use 'load-model:<path>' first.");
                return;
            }

            if (isNewNavigation)
            {
                if (tab.CurrentPage != null)
                    tab.History.Push(tab.CurrentPage);
                tab.ForwardHistory.Clear();
            }
            tab.CurrentUrl = url;

            if (tab == _activeTab)
            {
                _inputBox.Text = url;
                _inputBox.Select(_inputBox.Text.Length, 0);
                UpdateNavButtons();
            }

            tab.ConsoleBuffer.Clear();
            if (tab == _activeTab) RenderConsole();

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                using var resp = await _httpClient.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, tab.Cts.Token);
                resp.EnsureSuccessStatusCode();

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var html = DecodeHtml(bytes, resp.Content.Headers.ContentType?.CharSet);

                tab.RawHtml = html.Length > 50000 ? html.Substring(0, 50000) + "\n<!-- truncated -->" : html;

                var titleMatch = Regex.Match(
                    html, @"<title[^>]*>(.*?)</title>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var title = titleMatch.Success
                    ? System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim()
                    : "(no title)";

                tab.Title = title;
                if (tab.TabTitle != null)
                {
                    Dispatcher.Invoke(() => tab.TabTitle.Text = title);
                }

                ExtractLinks(tab, html);

                string rawText = HtmlToMarkdown(html, url);
                if (rawText.Length > _settings.MaxHtmlChars)
                    rawText = rawText.Substring(0, _settings.MaxHtmlChars) + "\n...";

                await LlmConvertToMarkdownAsync(tab, url, title, rawText);

                tab.ConsoleBuffer.AppendLine("");
                tab.ConsoleBuffer.AppendLine("-----------------------------------------------------------");
                tab.ConsoleBuffer.AppendLine($"`[i] Links on page: {tab.Links.Count}. Type 'links' to list, or a number to follow.`");
                tab.ConsoleBuffer.AppendLine("");

                tab.CurrentPage = new HistoryEntry
                {
                    Url = url,
                    Title = title,
                    MarkdownText = tab.ConsoleBuffer.ToString(),
                    Links = new List<(string, string)>(tab.Links)
                };

                if (tab == _activeTab) RenderConsole();
            }
            catch (OperationCanceledException) { if (tab == _activeTab) PrintInfo("[request cancelled]"); }
            catch (HttpRequestException hex) { if (tab == _activeTab) PrintError($"HTTP error: {hex.Message}"); }
            catch (Exception ex) { if (tab == _activeTab) PrintError($"Error: {ex.Message}"); }
        }

        private void RenderHistoryPage(BrowserTab tab)
        {
            tab.CurrentUrl = tab.CurrentPage.Url;
            if (tab == _activeTab)
            {
                _inputBox.Text = tab.CurrentUrl;
                _inputBox.Select(_inputBox.Text.Length, 0);

                tab.Links.Clear();
                tab.Links.AddRange(tab.CurrentPage.Links);

                tab.ConsoleBuffer.Clear();
                tab.ConsoleBuffer.Append(tab.CurrentPage.MarkdownText);
                RenderConsole();
                UpdateNavButtons();
            }
        }

        private async Task LlmConvertToMarkdownAsync(BrowserTab tab, string url, string title, string rawText)
        {
            if (!_modelLoaded || _executor == null)
            {
                if (tab == _activeTab) PrintError("Model not loaded.");
                return;
            }

            string selectedMode = "web";
            Dispatcher.Invoke(() =>
            {
                if (_modeComboBox.SelectedItem != null)
                    selectedMode = _modeComboBox.SelectedItem.ToString();
            });
            tab.Mode = selectedMode;

            string systemPrompt;
            string taskInstruction;

            if (selectedMode == "summary")
            {
                systemPrompt =
                    "You are a Markdown summarizer.\n\n" +
                    "TASK: Read the website text and write a short, well-formatted summary.\n\n" +
                    "OUTPUT SHAPE (copy this layout):\n" +
                    "## Site Name\n" +
                    "One or two sentences: what this site/page is about.\n\n" +
                    "### Key Points\n" +
                    "- point one\n" +
                    "- point two\n" +
                    "- point three\n\n" +
                    "RULES:\n" +
                    "1. Output Markdown only. No greetings. No explanations. No \"Here is the summary\".\n" +
                    "2. Start your reply with \"##\" immediately.\n" +
                    "3. Use \"##\" for the title and \"###\" for subheadings.\n" +
                    "4. Use \"-\" for every bullet point. One idea per bullet.\n" +
                    "5. Use \"**bold**\" only for a genuinely important word or number.\n" +
                    "6. Keep it short: a title, 1-2 sentence overview, then 3-6 bullets.\n" +
                    "7. Never repeat a line you already wrote.";
                taskInstruction = "Write the summary now, starting with \"##\":";
            }
            else
            {
                systemPrompt =
                    "You are a Markdown formatter.\n\n" +
                    "TASK: Turn the raw website text into a clean Markdown document.\n\n" +
                    "OUTPUT SHAPE (copy this layout):\n" +
                    "## Section Heading\n" +
                    "- First item on its own line\n" +
                    "- Second item on its own line\n" +
                    "- [Link Text](https://full-url.com)\n\n" +
                    "## Next Section Heading\n" +
                    "- Another item\n\n" +
                    "RULES:\n" +
                    "1. Output Markdown only. No greetings. No explanations. No \"Here is the text\".\n" +
                    "2. Start your reply with \"##\" immediately.\n" +
                    "3. One article, headline, or item per line. Never merge several items into one paragraph.\n" +
                    "4. Use \"##\" for each section heading.\n" +
                    "5. Use \"-\" at the start of every list item.\n" +
                    "6. Write every link exactly as [Link Text](https://full-url.com). No space between ] and (.\n" +
                    "7. Include every article, section, and link from the text. Skip nothing.\n" +
                    "8. Never repeat a line you already wrote. If you catch yourself repeating, stop.\n" +
                    "9. No HTML tags. Markdown syntax only (**bold**, *italic*).";
                taskInstruction = "Format the content now, starting with \"##\":";
            }

            string userPrompt =
                $"URL: {url}\n" +
                $"Title: {title}\n\n" +
                "Raw website content:\n" +
                "----------------------------------------\n" +
                rawText + "\n" +
                "----------------------------------------\n\n" +
                taskInstruction;

            string prompt =
                $"<|im_start|>system\n{systemPrompt}<|im_end|>\n" +
                $"<|im_start|>user\n{userPrompt}<|im_end|>\n" +
                $"<|im_start|>assistant\n##";

            var samplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = _settings.Temperature,
                TopK = _settings.TopK,
                TopP = _settings.TopP,
                RepeatPenalty = _settings.RepeatPenalty
            };

            var inferenceParams = new InferenceParams
            {
                MaxTokens = _settings.MaxLLMOutputTokens,
                AntiPrompts = new List<string> { "<|im_end|>" },
                SamplingPipeline = samplingPipeline
            };

            if (tab == _activeTab)
            {
                Dispatcher.Invoke(() => _spinnerBlock.Visibility = Visibility.Visible);
            }

            var spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            int spinIdx = 0;
            string[] spinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
            spinnerTimer.Tick += (s, e) =>
            {
                spinIdx = (spinIdx + 1) % spinnerFrames.Length;
                if (tab == _activeTab) _spinnerBlock.Text = spinnerFrames[spinIdx];
            };
            spinnerTimer.Start();

            var sb = new StringBuilder("##");

            try
            {
                await foreach (var token in _executor.InferAsync(prompt, inferenceParams).ConfigureAwait(false))
                {
                    if (tab.Cts?.IsCancellationRequested == true) break;
                    sb.Append(token);
                }
            }
            catch (Exception ex)
            {
                sb.Append($"\n[LLM Error: {ex.Message}]");
            }

            spinnerTimer.Stop();

            if (tab == _activeTab)
            {
                Dispatcher.Invoke(() =>
                {
                    _spinnerBlock.Visibility = Visibility.Collapsed;
                    _spinnerBlock.Text = "";
                });
            }

            // NOTE: forced GC.Collect()/WaitForPendingFinalizers() removed —
            // a blocking full collection here on every single generation was
            // a major source of UI stutter. The .NET GC already runs
            // generationally and efficiently on its own; forcing a full
            // synchronous collection after every request defeats that.

            string finalText = sb.ToString().Replace("```markdown", "").Replace("```", "").Trim();

            int assistantTag = finalText.IndexOf("<|im_start|>assistant");
            if (assistantTag >= 0)
            {
                finalText = finalText.Substring(assistantTag + "<|im_start|>assistant".Length).Trim();
            }

            finalText = RemoveRepetitiveLoops(finalText);

            tab.ConsoleBuffer.AppendLine(finalText);
            if (tab == _activeTab) RenderConsole();
        }

        private string RemoveRepetitiveLoops(string text)
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var cleanedLines = new List<string>();
            string lastLine = "";
            int repeatCount = 1;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (cleanedLines.Count == 0 || !string.IsNullOrWhiteSpace(cleanedLines.Last()))
                    {
                        cleanedLines.Add(line);
                    }
                    continue;
                }

                string normalizedLine = line.Trim().ToLowerInvariant();

                if (normalizedLine == lastLine)
                {
                    repeatCount++;
                    if (repeatCount >= 3)
                    {
                        cleanedLines.Add("... [Content truncated due to repetition] ...");
                        break;
                    }
                }
                else
                {
                    repeatCount = 1;
                    lastLine = normalizedLine;
                }
                cleanedLines.Add(line);
            }

            return string.Join("\n", cleanedLines);
        }

        private void RenderConsole()
        {
            if (_activeTab == null) return;

            string sanitizedText = SanitizeMarkdown(_activeTab.ConsoleBuffer.ToString());

            FlowDocument doc = _markdownEngine.Transform(sanitizedText);

            doc.Background = BgBrush;
            doc.Foreground = TextBrush;
            doc.FontFamily = AppFont;
            doc.FontSize = 14;

            doc.Resources[typeof(Table)] = TableStyle;
            doc.Resources[typeof(TableCell)] = TableCellStyle;
            doc.Resources[typeof(Hyperlink)] = HyperlinkStyle;

            AttachHyperlinkHandlers(doc);

            _outputBox.Document = doc;
            _outputBox.ScrollToEnd();
        }

        private void AttachHyperlinkHandlers(FlowDocument doc)
        {
            int hyperlinkCount = 0;
            void WalkInline(Inline inline)
            {
                if (inline is Hyperlink hl)
                {
                    hyperlinkCount++;
                    hl.Cursor = Cursors.Hand;
                    hl.ForceCursor = true;
                    hl.Click -= Hyperlink_Click;
                    hl.Click += Hyperlink_Click;
                    foreach (var child in hl.Inlines) WalkInline(child);
                }
                else if (inline is Span span)
                {
                    foreach (var child in span.Inlines) WalkInline(child);
                }
            }

            void WalkBlock(Block block)
            {
                switch (block)
                {
                    case Paragraph p:
                        foreach (var inline in p.Inlines) WalkInline(inline);
                        break;
                    case Section sec:
                        foreach (var b in sec.Blocks) WalkBlock(b);
                        break;
                    case List list:
                        foreach (var li in list.ListItems)
                            foreach (var b in li.Blocks) WalkBlock(b);
                        break;
                    case Table table:
                        foreach (var rg in table.RowGroups)
                            foreach (var r in rg.Rows)
                                foreach (var c in r.Cells)
                                    foreach (var b in c.Blocks) WalkBlock(b);
                        break;
                }
            }

            foreach (var block in doc.Blocks) WalkBlock(block);
            Debug.WriteLine($"[link-render] {hyperlinkCount} Hyperlink element(s) built into FlowDocument");
        }
        private string SanitizeMarkdown(string text)
        {
            text = Regex.Replace(text, @"\]\s+\(", "](");

            int codeBlocks = Regex.Matches(text, "```").Count;
            if (codeBlocks % 2 != 0)
            {
                text += "\n```";
            }

            var lines = text.Split('\n');
            var cleanedLines = new List<string>(lines.Length);
            int tablePipeCount = -1;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (Regex.IsMatch(trimmed, @"\[([^\]]+)\]\([^)\s]*$") && !trimmed.EndsWith(")"))
                {
                    trimmed = Regex.Replace(trimmed, @"\[([^\]]+)\]\(([^)]*)$", "$1");
                }

                if (trimmed.StartsWith("|"))
                {
                    int pipes = trimmed.Count(c => c == '|');
                    if (tablePipeCount == -1)
                    {
                        if (pipes >= 2)
                        {
                            tablePipeCount = pipes;
                            cleanedLines.Add(line);
                        }
                        else
                        {
                            cleanedLines.Add(trimmed.Replace("|", " "));
                        }
                    }
                    else
                    {
                        if (Regex.IsMatch(trimmed, @"^\|[\s\-:]+\|"))
                        {
                            cleanedLines.Add(line);
                        }
                        else if (pipes == tablePipeCount)
                        {
                            cleanedLines.Add(line);
                        }
                        else
                        {
                            cleanedLines.Add(trimmed.Replace("|", " "));
                            tablePipeCount = -1;
                        }
                    }
                }
                else
                {
                    tablePipeCount = -1;
                    cleanedLines.Add(line);
                }
            }

            return string.Join("\n", cleanedLines);
        }

        private string DecodeHtml(byte[] bytes, string charset)
        {
            Encoding enc = Encoding.UTF8;
            if (!string.IsNullOrEmpty(charset))
            {
                try { enc = Encoding.GetEncoding(charset); } catch { }
            }
            else
            {
                var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
                var m = Regex.Match(head, @"charset\s*=\s*[""']?\s*([a-zA-Z0-9\-_]+)",
                                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    try { enc = Encoding.GetEncoding(m.Groups[1].Value); } catch { }
                }
            }
            return enc.GetString(bytes);
        }

        private string HtmlToMarkdown(string html, string baseUrl)
        {
            html = Regex.Replace(html, @"(?is)<script[^>]*>.*?</script>", " ");
            html = Regex.Replace(html, @"(?is)<style[^>]*>.*?</style>", " ");
            html = Regex.Replace(html, @"(?s)<!--.*?-->", " ");
            html = Regex.Replace(html, @"(?is)<noscript[^>]*>.*?</noscript>", " ");
            html = Regex.Replace(html, @"(?is)<svg[^>]*>.*?</svg>", " ");
            html = Regex.Replace(html, @"(?is)<nav[^>]*>.*?</nav>", " ");
            html = Regex.Replace(html, @"(?is)<footer[^>]*>.*?</footer>", " ");
            html = Regex.Replace(html, @"(?is)<header[^>]*>.*?</header>", " ");
            html = Regex.Replace(html, @"(?is)<aside[^>]*>.*?</aside>", " ");
            html = Regex.Replace(html, @"(?is)<form[^>]*>.*?</form>", " ");

            for (int i = 6; i >= 1; i--)
            {
                var hashes = new string('#', i);
                html = Regex.Replace(html, $@"(?is)<h{i}[^>]*>(.*?)</h{i}>",
                    m => $"\n\n{hashes} {CleanInline(m.Groups[1].Value)}\n\n");
            }

            html = Regex.Replace(html,
                @"(?is)<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
                m =>
                {
                    var linkText = CleanInline(m.Groups[2].Value);
                    var href = m.Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(linkText)) linkText = href;

                    if (string.IsNullOrWhiteSpace(href) ||
                        href.StartsWith("#") ||
                        href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                        href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                        href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                    {
                        return linkText;
                    }

                    // Resolve relative hrefs (the overwhelming majority in real
                    // HTML) to absolute URLs so MdXaml can build a valid
                    // Hyperlink.NavigateUri. Without this, relative hrefs
                    // produce no clickable link at all.
                    string absHref = ResolveUrl(href, baseUrl);

                    // Markdown link syntax breaks on unescaped parentheses in
                    // the URL (common in e.g. Wikipedia links), so percent-
                    // encode them; this is a no-op for URLs without parens.
                    absHref = absHref.Replace("(", "%28").Replace(")", "%29");

                    return $"[{linkText}]({absHref})";
                });

            html = Regex.Replace(html, @"(?is)<ul[^>]*>(.*?)</ul>", m => m.Groups[1].Value);
            html = Regex.Replace(html, @"(?is)<ol[^>]*>(.*?)</ol>", m => m.Groups[1].Value);
            html = Regex.Replace(html, @"(?is)<li[^>]*>(.*?)</li>",
                m => "- " + CleanInline(m.Groups[1].Value) + "\n");

            html = Regex.Replace(html, @"(?is)<(strong|b)\b[^>]*>(.*?)</\1>",
                m => "**" + m.Groups[2].Value + "**");
            html = Regex.Replace(html, @"(?is)<(em|i)\b[^>]*>(.*?)</\1>",
                m => "*" + m.Groups[2].Value + "*");
            html = Regex.Replace(html, @"(?is)<code\b[^>]*>(.*?)</code>",
                m => "`" + m.Groups[1].Value + "`");

            html = Regex.Replace(html, @"(?is)<blockquote[^>]*>(.*?)</blockquote>",
                m => "\n> " + m.Groups[1].Value.Replace("\n", "\n> ") + "\n");

            html = Regex.Replace(html, @"(?i)<br\s*/?>", "\n");
            html = Regex.Replace(html, @"(?is)</p>", "\n\n");
            html = Regex.Replace(html, @"(?is)</div>", "\n");

            html = Regex.Replace(html, @"<[^>]+>", " ");
            html = System.Net.WebUtility.HtmlDecode(html);

            html = Regex.Replace(html, @"[ \t]+", " ");
            html = Regex.Replace(html, @" *\n *", "\n");
            html = Regex.Replace(html, @"\n{3,}", "\n\n");

            return html.Trim();
        }

        private string CleanInline(string s)
        {
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = System.Net.WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private void ExtractLinks(BrowserTab tab, string html)
        {
            tab.Links.Clear();
            var matches = Regex.Matches(
                html,
                @"(?is)<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>");
            foreach (Match m in matches)
            {
                if (tab.Links.Count >= 100) break;
                var href = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (href.StartsWith("#")) continue;
                if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;
                var text = CleanInline(m.Groups[2].Value);
                if (string.IsNullOrWhiteSpace(text)) text = href;
                tab.Links.Add((text, href));
            }
        }

        private string ResolveUrl(string href, string baseUrl)
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                if (Uri.TryCreate(baseUri, href, out var resolved))
                    return resolved.AbsoluteUri;
            return href;
        }

        private void Print(string text) => AppendOutput(text);
        private void PrintLine(string text) => AppendOutput(text);
        private void PrintInfo(string text) => AppendOutput($"`[i] {text}`");
        private void PrintSuccess(string text) => AppendOutput($"`[+] {text}`");
        private void PrintError(string text) => AppendOutput($"`[!] {text}`");

        private void AppendOutput(string text)
        {
            _activeTab.ConsoleBuffer.AppendLine(text);
            RenderConsole();
        }
    }
}