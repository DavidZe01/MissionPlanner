// The Flight Data tab panel (under the HUD) in a macOS style, for the native macOS build only.
//
// Visual only: the same TabPages (names, order, SelectedIndex, Ctrl+1..0), the same controls and the
// same event handlers. What changes:
//   - the tab strip is hidden and a native pop-up menu (NSPopUpButton) above the panel picks the page;
//   - Actions is laid out to the pane's width (choice + button rows, a grid of equal buttons, value +
//     button rows) and scrolls only vertically;
//   - Servo/Relay and Aux Function are a single column of rows as wide as the pane, each row's
//     buttons grouped, its fields on a second line when they do not fit;
//   - buttons get rounded corners; native controls follow the theme's light or dark background.
#if MACNATIVE
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;

namespace MissionPlanner.GCSViews
{
    public partial class FlightData
    {
        const int MacMargin = 8, MacGap = 6, MacSectionGap = 12, MacCornerRadius = 6, MacRowHeight = 28;

        ComboBox macTabPicker;
        Panel macTabHeader;
        Panel macActions;
        bool macTabPickerUpdating, macActionsLayingOut;
        readonly List<KeyValuePair<ComboBox, Control>> macActionPairs = new List<KeyValuePair<ComboBox, Control>>();
        readonly List<Control> macActionButtons = new List<Control>();
        readonly List<ModifyandSet> macActionValues = new List<ModifyandSet>();
        readonly HashSet<Control> macWatchedPages = new HashSet<Control>();
        static readonly HashSet<Control> macRowsLayingOut = new HashSet<Control>();

        /// <summary>The pop-up menu that picks the page (the screen tour checks it).</summary>
        internal ComboBox MacTabPicker => macTabPicker;
        /// <summary>The panel Actions' controls are laid out in (the screen tour checks it).</summary>
        internal Panel MacActions => macActions;

        private void MacStyleActionsPanel()
        {
            if (macTabPicker != null)
                return;
            MacTabSelector();
            MacActionsLayout();
            MacOptionList(flowLayoutPanelServos);
            MacOptionList(flowLayoutPanel1);
            MacButtonShapes(tabControlactions);
            // pages added later (a plugin's) get the same buttons
            tabControlactions.ControlAdded += (sender, e) => MacButtonShapes(e.Control);
            // ThemeManager sets the colours again when the theme changes
            tabControlactions.BackColorChanged += (sender, e) => MacPanelAppearance();
            SubMainLeft.Panel2.BackColorChanged += (sender, e) => MacPanelAppearance();
            SubMainLeft.Panel2.HandleCreated += (sender, e) => MacPanelAppearance();
            MacPanelAppearance();
            MacDefaultPaneWidth();
        }

        #region Page picker

        private void MacTabSelector()
        {
            tabControlactions.TabStripVisible = false;

            macTabPicker = new ComboBox
            {
                Name = "macTabPicker",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                ContextMenuStrip = contextMenuStripactionstab,
            };
            macTabHeader = new Panel
            {
                Name = "macTabHeader",
                Dock = DockStyle.Top,
                Height = macTabPicker.PreferredHeight + 2 * MacGap,
                Padding = new Padding(MacMargin, MacGap, MacMargin, MacGap),
                ContextMenuStrip = contextMenuStripactionstab,
            };
            macTabHeader.Controls.Add(macTabPicker);

            // docked after (below) panel_persistent and before the tab control fills the rest
            var panel = SubMainLeft.Panel2;
            panel.Controls.Add(macTabHeader);
            panel.Controls.SetChildIndex(macTabHeader, panel.Controls.GetChildIndex(tabControlactions) + 1);

            // without a tab strip there is nothing to show on several lines (the setting is kept)
            multiLineToolStripMenuItem.Visible = false;

            macTabPicker.SelectedIndexChanged += (sender, e) =>
            {
                if (macTabPickerUpdating)
                    return;
                int index = macTabPicker.SelectedIndex;
                if (index >= 0 && index < tabControlactions.TabCount && tabControlactions.SelectedIndex != index)
                    tabControlactions.SelectedIndex = index;
            };
            tabControlactions.SelectedIndexChanged += (sender, e) => MacSyncTabPicker();
            tabControlactions.ControlAdded += (sender, e) =>
            {
                MacWatchPage(e.Control);
                MacRebuildTabPicker();
            };
            tabControlactions.ControlRemoved += (sender, e) => MacRebuildTabPicker();
            foreach (TabPage page in tabControlactions.TabPages)
                MacWatchPage(page);
            MacRebuildTabPicker();
        }

        private void MacWatchPage(Control page)
        {
            if (macWatchedPages.Add(page))
                page.TextChanged += (sender, e) => MacRebuildTabPicker();
        }

        // the two pages called "Actions" are told apart in the menu only
        private string MacTabTitle(TabPage page)
        {
            return page == tabActionsSimple ? page.Text + " (simple)" : page.Text;
        }

        private void MacRebuildTabPicker()
        {
            macTabPickerUpdating = true;
            try
            {
                macTabPicker.BeginUpdate();
                macTabPicker.Items.Clear();
                foreach (TabPage page in tabControlactions.TabPages)
                    macTabPicker.Items.Add(MacTabTitle(page));
                macTabPicker.EndUpdate();
                int index = tabControlactions.SelectedIndex;
                macTabPicker.SelectedIndex = index >= 0 && index < macTabPicker.Items.Count ? index : -1;
                macTabHeader.Visible = tabControlactions.TabCount > 0;
            }
            finally
            {
                macTabPickerUpdating = false;
            }
        }

        private void MacSyncTabPicker()
        {
            if (macTabPicker.Items.Count != tabControlactions.TabCount)
            {
                MacRebuildTabPicker();
                return;
            }
            macTabPickerUpdating = true;
            try
            {
                macTabPicker.SelectedIndex = tabControlactions.SelectedIndex;
            }
            finally
            {
                macTabPickerUpdating = false;
            }
        }

        #endregion

        #region Appearance

        private static IEnumerable<Control> MacDescendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (var grandchild in MacDescendants(child))
                    yield return grandchild;
            }
        }

        private static void MacButtonShapes(Control root)
        {
            if (root is MyButton self)
                self.CornerRadius = MacCornerRadius;
            foreach (var button in MacDescendants(root).OfType<MyButton>())
                button.CornerRadius = MacCornerRadius;
        }

        // native controls (pop-up menus, text fields, steppers, scroll bars) dark on a dark theme
        private void MacPanelAppearance()
        {
            var background = tabControlactions.TabCount > 0 ? tabControlactions.TabPages[0].BackColor : SubMainLeft.Panel2.BackColor;
            if (background.A < 255)
                background = SubMainLeft.Panel2.BackColor;
            bool dark = background.GetBrightness() < 0.5f;
            if (macTabHeader != null)
                macTabHeader.BackColor = background;
            MacNative.SetAppearance(SubMainLeft.Panel2, dark);
        }

        private static readonly PropertyInfo macVisibleInternal =
            typeof(Control).GetProperty("VisibleInternal", BindingFlags.NonPublic | BindingFlags.Instance);

        // the control's own Visible flag, also while its page is hidden (Visible is then false)
        private static bool MacShown(Control control)
        {
            return macVisibleInternal != null ? (bool)macVisibleInternal.GetValue(control) : control.Visible;
        }

        #endregion

        #region Actions

        private void MacActionsLayout()
        {
            var table = tableLayoutPanel1;
            var controls = table.Controls.Cast<Control>().ToList();
            Func<Control, TableLayoutPanelCellPosition> cell = c => table.GetCellPosition(c);

            // a choice and the button beside it, a value and its button, and the other buttons in
            // the grid's reading order down each column (Auto, Loiter, RTL, ... as on Windows)
            foreach (var combo in controls.OfType<ComboBox>().OrderBy(c => cell(c).Row))
            {
                var position = cell(combo);
                var button = controls.FirstOrDefault(c => !(c is ComboBox) && !(c is ModifyandSet) &&
                    cell(c).Row == position.Row && cell(c).Column == position.Column + 1);
                macActionPairs.Add(new KeyValuePair<ComboBox, Control>(combo, button));
            }
            macActionValues.AddRange(controls.OfType<ModifyandSet>().OrderBy(c => cell(c).Row));
            macActionButtons.AddRange(controls.Where(c => !(c is ComboBox) && !(c is ModifyandSet) &&
                    !macActionPairs.Any(p => p.Value == c))
                .OrderBy(c => cell(c).Column).ThenBy(c => cell(c).Row));

            macActions = new Panel { Name = "macActions", Dock = DockStyle.Fill, AutoScroll = true };
            tabActions.Controls.Add(macActions);
            macActions.BringToFront();
            table.Visible = false;

            foreach (var control in controls)
            {
                macActions.Controls.Add(control);
                control.Dock = DockStyle.None;
                control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                control.Margin = Padding.Empty;
                control.VisibleChanged += (sender, e) => macActions.PerformLayout();
                control.TextChanged += (sender, e) => macActions.PerformLayout();
            }
            foreach (var modify in macActionValues)
            {
                var number = modify.NumericUpDown;
                string format = "F" + number.DecimalPlaces;
                int textWidth = Math.Max(
                    TextRenderer.MeasureText(number.Minimum.ToString(format), number.Font).Width,
                    TextRenderer.MeasureText(number.Maximum.ToString(format), number.Font).Width);
                number.Width = MacUpDownWidth(number, textWidth);
                modify.AutoSize = false;
                modify.MinimumSize = Size.Empty;
                FitModifyAndSet(modify);
            }
            foreach (var pair in macActionPairs)
                pair.Key.DropDown += ActionComboDropDown;

            macActions.Layout += (sender, e) => MacLayoutActions();
            macActions.Resize += (sender, e) => MacLayoutActions();
            MacLayoutActions();
        }

        private static int MacButtonWidth(Control control)
        {
            return Math.Max(64, TextRenderer.MeasureText(control.Text, control.Font, Size.Empty,
                TextFormatFlags.SingleLine).Width + 20);
        }

        /// <summary>The width one column of Actions' buttons needs.</summary>
        internal int MacActionColumnWidth
        {
            get { return macActionButtons.Select(MacButtonWidth).DefaultIfEmpty(64).Aggregate(Math.Max); }
        }

        private void MacLayoutActions()
        {
            if (macActionsLayingOut || macActions == null)
                return;
            int width = macActions.ClientSize.Width - 2 * MacMargin;
            if (width <= 0)
                return;
            macActionsLayingOut = true;
            try
            {
                int top = macActions.AutoScrollPosition.Y;
                int y = MacMargin;

                // a choice and its button on each row: the choice takes what the button leaves
                var pairs = macActionPairs.Where(p => MacShown(p.Key)).ToList();
                int pairButton = pairs.Where(p => p.Value != null).Select(p => MacButtonWidth(p.Value))
                    .DefaultIfEmpty(0).Aggregate(Math.Max);
                pairButton = Math.Min(pairButton, width / 2);
                foreach (var pair in pairs)
                {
                    var combo = pair.Key;
                    var button = pair.Value;
                    int comboWidth = button != null && MacShown(button) ? width - pairButton - MacGap : width;
                    combo.Bounds = new Rectangle(MacMargin, top + y + (MacRowHeight - combo.Height) / 2, comboWidth, combo.Height);
                    if (button != null)
                        button.Bounds = new Rectangle(MacMargin + width - pairButton, top + y, pairButton, MacRowHeight);
                    y += MacRowHeight + MacGap;
                }
                if (pairs.Count > 0)
                    y += MacSectionGap - MacGap;

                // the buttons in equal columns, as many as fit (1 to 4)
                var buttons = macActionButtons.Where(MacShown).ToList();
                if (buttons.Count > 0)
                {
                    int column = MacActionColumnWidth;
                    int columns = Math.Max(1, Math.Min(4, (width + MacGap) / (column + MacGap)));
                    int cellWidth = (width - (columns - 1) * MacGap) / columns;
                    for (int i = 0; i < buttons.Count; i++)
                        buttons[i].Bounds = new Rectangle(MacMargin + (i % columns) * (cellWidth + MacGap),
                            top + y + (i / columns) * (MacRowHeight + MacGap), cellWidth, MacRowHeight);
                    int rows = (buttons.Count + columns - 1) / columns;
                    y += rows * (MacRowHeight + MacGap) - MacGap + MacSectionGap;
                }

                // a value and its button on each row
                foreach (var modify in macActionValues.Where(MacShown))
                {
                    modify.Bounds = new Rectangle(MacMargin, top + y, width, MacRowHeight);
                    y += MacRowHeight + MacGap;
                }

                macActions.AutoScrollMinSize = new Size(0, y - MacGap + MacMargin);
            }
            finally
            {
                macActionsLayingOut = false;
            }
        }

        // With no saved splitter, a pane wide enough for three columns of Actions' buttons (up to 2/3
        // of the window); a saved width is the user's and is kept.
        private void MacDefaultPaneWidth()
        {
            if (Settings.Instance.ContainsKey("FlightSplitter"))
                return;
            int borders = Math.Max(0, MainH.SplitterDistance - SubMainLeft.Panel2.ClientSize.Width);
            int wanted = 3 * MacActionColumnWidth + 2 * MacGap + 2 * MacMargin + SystemInformation.VerticalScrollBarWidth + borders;
            int maximum = Math.Max(MainH.Panel1MinSize, (MainH.Width - MainH.SplitterWidth) * 2 / 3);
            MainH.SplitterDistance = Math.Max(MainH.Panel1MinSize, Math.Min(wanted, maximum));
        }

        // The width a Cocoa NumericUpDown needs to show textWidth of text: its text field (whose bezel
        // and cell insets take more room than Windows' edit box), the gap and the stepper beside it.
        internal static int MacUpDownWidth(NumericUpDown number, int textWidth)
        {
            const int fieldInsets = 12, gap = 4;
            int stepper = number.Controls.Cast<Control>().Where(c => !(c is TextBoxBase))
                .Select(c => c.PreferredSize.Width).DefaultIfEmpty(SystemInformation.VerticalScrollBarWidth).Aggregate(Math.Max);
            var field = number.Controls.OfType<TextBoxBase>().FirstOrDefault();
            int borders = field == null ? 0 : field.Width - field.ClientSize.Width + field.Padding.Horizontal;
            return textWidth + borders + fieldInsets + gap + stepper;
        }

        // A ModifyandSet's number keeps its width and its button takes the rest of the row, on the
        // same line (its FlowLayoutPanel would otherwise wrap the button out of sight).
        private static void FitModifyAndSet(ModifyandSet modify)
        {
            var row = modify.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            if (row != null)
                row.WrapContents = false;
            modify.Button.AutoSize = false;
            EventHandler fit = (sender, e) =>
            {
                var number = modify.NumericUpDown;
                number.Margin = new Padding(0, Math.Max(0, (modify.ClientSize.Height - number.Height) / 2), MacGap, 0);
                modify.Button.Margin = Padding.Empty;
                modify.Button.Width = Math.Max(0, modify.ClientSize.Width - number.Width - number.Margin.Horizontal);
                modify.Button.Height = modify.ClientSize.Height;
            };
            modify.Resize += fit;
            fit(modify, EventArgs.Empty);
        }

        #endregion

        #region Servo/Relay and Aux Function

        // a single column of rows, each as wide as the pane: nothing to scroll sideways
        private static void MacOptionList(ScrollableControl list)
        {
            if (list is FlowLayoutPanel flow)
            {
                flow.FlowDirection = FlowDirection.TopDown;
                flow.WrapContents = false;
            }
            list.AutoScroll = true;
            list.Padding = new Padding(MacMargin, MacMargin / 2, MacMargin, MacMargin / 2);
            foreach (Control row in list.Controls)
                MacOptionRow(row);
            list.ControlAdded += (sender, e) =>
            {
                MacOptionRow(e.Control);
                MacFitOptionRows(list);
            };
            list.Resize += (sender, e) => MacFitOptionRows(list);
            MacFitOptionRows(list);
        }

        private static void MacFitOptionRows(ScrollableControl list)
        {
            int width = list.ClientSize.Width - list.Padding.Horizontal;
            if (width <= 0)
                return;
            foreach (Control row in list.Controls)
            {
                int rowWidth = Math.Max(0, width - row.Margin.Horizontal);
                if (row.Width != rowWidth)
                    row.Width = rowWidth;
            }
        }

        // the controls of a ServoOptions, RelayOptions or AuxOptions row, by their designer names
        static readonly string[] macOptionLead = { "mavlinkComboBox1" };
        static readonly string[] macOptionButtons = { "BUT_Low", "but_mid", "BUT_High", "BUT_Repeat" };
        static readonly string[] macOptionLight = { "TXT_rcchannel" };
        static readonly string[] macOptionFields = { "TXT_pwm_low", "TXT_low_value", "txt_midvalue", "TXT_pwm_high", "TXT_highvalue" };

        private static Control[] MacFind(Control row, string[] names)
        {
            return names.Select(n => row.Controls[n]).Where(c => c != null).ToArray();
        }

        private static void MacOptionRow(Control row)
        {
            if (MacFind(row, macOptionButtons).Length == 0)
                return;
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            foreach (var light in MacFind(row, macOptionLight).OfType<Label>())
            {
                light.AutoSize = false;
                light.TextAlign = ContentAlignment.MiddleCenter;
            }
            row.Layout += (sender, e) => MacLayoutOptionRow(row);
            MacLayoutOptionRow(row);
        }

        private static void MacLayoutOptionRow(Control row)
        {
            int width = row.ClientSize.Width;
            if (width <= 0 || !macRowsLayingOut.Add(row))
                return;
            try
            {
                const int height = 24, joined = 2, fieldWidth = 52;
                var lead = MacFind(row, macOptionLead).FirstOrDefault();
                var buttons = MacFind(row, macOptionButtons).Where(MacShown).ToArray();
                var light = MacFind(row, macOptionLight).FirstOrDefault();
                var fields = MacFind(row, macOptionFields).Where(MacShown).ToArray();

                var buttonWidths = buttons.Select(b => Math.Max(36, TextRenderer.MeasureText(b.Text, b.Font).Width + 14)).ToArray();
                int buttonsWidth = buttonWidths.Sum() + Math.Max(0, buttons.Length - 1) * joined;
                int lightWidth = light == null ? 0 : Math.Max(54, TextRenderer.MeasureText(light.Text, light.Font).Width + 12);

                int x = 0, y = MacGap / 2;
                if (lead != null)
                {
                    int rest = buttonsWidth + MacGap + (light != null ? lightWidth + MacGap : 0);
                    int leadWidth = Math.Max(90, Math.Min(170, width - rest));
                    lead.Bounds = new Rectangle(0, y + (height - lead.Height) / 2, leadWidth, lead.Height);
                    x = leadWidth + MacGap;
                }
                int buttonsLeft = x;
                // the buttons side by side as one group
                for (int i = 0; i < buttons.Length; i++)
                {
                    buttons[i].Bounds = new Rectangle(x, y, buttonWidths[i], height);
                    x += buttonWidths[i] + joined;
                }
                x += MacGap - joined;
                if (light != null)
                {
                    light.Bounds = new Rectangle(x, y + 2, lightWidth, height - 4);
                    x += lightWidth + MacGap;
                }
                // the fields after them, or on a second line under the buttons when they do not fit
                int fieldsWidth = fields.Length * fieldWidth + Math.Max(0, fields.Length - 1) * 4;
                if (fields.Length > 0 && x + fieldsWidth > width)
                {
                    x = buttonsLeft;
                    y += height + 4;
                }
                foreach (var field in fields)
                {
                    field.Bounds = new Rectangle(x, y + (height - field.Height) / 2, fieldWidth, field.Height);
                    x += fieldWidth + 4;
                }
                int rowHeight = y + height + MacGap / 2;
                if (row.Height != rowHeight)
                    row.Height = rowHeight;
            }
            finally
            {
                macRowsLayingOut.Remove(row);
            }
        }

        #endregion
    }
}
#endif
