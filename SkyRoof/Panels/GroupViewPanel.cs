using System.Data;
using Serilog;
using VE3NEA;
using WeifenLuo.WinFormsUI.Docking;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace SkyRoof
{
  public partial class GroupViewPanel : DockContent
  {
    public class ItemData
    {
      public SatnogsDbSatellite Sat;
      public SatellitePass? Pass;
      public ItemData(SatnogsDbSatellite sat) { Sat = sat; }
    }

    protected readonly Context ctx;
    private int SortColumn = 2;
    public ListViewItem[] Items;
    // shared, so we don't leak a GDI font handle per item on every rebuild
    private Font BoldFont, StrikeoutFont;
    // sat the context menu was opened on, captured at Opening time (see SatelliteDetailsMNU_Click)
    private SatnogsDbSatellite? ContextMenuSat;
    // shown only over the AMSAT status icon, the row tooltip is suppressed meanwhile.
    // It is shown by hand for the panel, never attached to the list: a ToolTip attached
    // to a ListView replaces the list's native item tooltip
    private System.Windows.Forms.ToolTip? AmsatToolTip;
    private System.Windows.Forms.Timer? AmsatToolTipTimer;
    private ListViewItem? AmsatToolTipItem;



    public GroupViewPanel()
    {
      InitializeComponent();
    }

    public GroupViewPanel(Context ctx)
    {
      Log.Information("Creating GroupViewPanel");
      this.ctx = ctx;
      InitializeComponent();

      // match the list's native item tooltip: visual-styled in the light theme,
      // repainted in the dark one (see ListViewEx.ThemeTooltip)
      AmsatToolTip = Theme.IsDark ? new ToolTipEx(components!) : new System.Windows.Forms.ToolTip(components!);
      AmsatToolTipTimer = new System.Windows.Forms.Timer(components!) { Interval = AmsatToolTip.InitialDelay };
      AmsatToolTipTimer.Tick += AmsatToolTipTimer_Tick;
      listView1.MouseMove += listView1_MouseMove;
      listView1.MouseLeave += listView1_MouseLeave;

      this.ctx.GroupViewPanel = this;
      this.ctx.MainForm.GroupViewMNU.Checked = true;
      ctx.Settings.Ui.RestoreColumnWidths("GroupViewPanel", listView1);
      LoadGroup();
    }

    private void GroupViewPanel_FormClosing(object sender, FormClosingEventArgs e)
    {
      Log.Information("Closing GroupViewPanel");
      ctx.GroupViewPanel = null;
      ctx.MainForm.GroupViewMNU.Checked = false;
      ctx.Settings.Ui.SaveColumnWidths("GroupViewPanel", listView1);
    }

    public void LoadGroup()
    {
      // select group or default
      var sett = ctx.Settings.Satellites;
      var group = sett.SatelliteGroups.First(g => g.Id == sett.SelectedGroupId);
      Items = group.SatelliteIds
        .Select(id => ctx.SatnogsDb.GetSatellite(id))
        .Where(s => s != null)
        .Cast<SatnogsDbSatellite>()
        .Select(ItemFromSat)
        .ToArray();
      ShowAmsatStatuses();

      listView1.VirtualListSize = Items.Length;
      listView1.Invalidate();

      ShowSelectedSat();
      GroupNameLabel.Text = $"Group:   {group.Name}";
    }

    private bool TryGetSelectedItem(out ItemData data)
    {
      data = null!;
      if (Items == null || Items.Length == 0) return false;
      if (listView1.SelectedIndices.Count == 0) return false;

      int idx = listView1.SelectedIndices[0];
      if (idx < 0 || idx >= Items.Length) return false;
      if (Items[idx].Tag is not ItemData itemData) return false;

      data = itemData;
      return true;
    }

    private ListViewItem ItemFromSat(SatnogsDbSatellite sat)
    {
      var item = new ListViewItem([
        sat.name,
        sat.norad_cat_id?.ToString() ?? "",
        "",
        "",
        ]);

      item.Tag = new ItemData(sat);
      item.ToolTipText = sat.GetTooltipText();

      if (sat.Transmitters.Any(t => t.IsUhf() && t.HasUplink())) item.BackColor = Theme.UhfTint;
      else if (sat.Transmitters.Any(t => t.IsVhf() && t.HasUplink())) item.BackColor = Theme.VhfTint;

      BoldFont ??= new Font(listView1.Font, FontStyle.Bold);
      StrikeoutFont ??= new Font(listView1.Font, FontStyle.Strikeout);

      if (sat.Flags.HasFlag(SatelliteFlags.Ham)) item.Font = BoldFont;
      item.ForeColor = Theme.RowText(!sat.IsAlive());
      if (sat.IsAlive() && sat.Tle == null) item.Font = StrikeoutFont;

      return item;
    }

    public void ShowSelectedSat()
    {
      if (Items == null) return;

      var selectedSat = ctx.SatelliteSelector.SelectedSatellite;

      foreach (var item in Items)
        item.StateImageIndex = ((ItemData)item.Tag!).Sat == selectedSat ? 0 : -1;

      listView1.SelectedIndices.Clear();
      listView1.Invalidate();
    }

    public void ShowAmsatStatuses()
    {
      if (!ctx.Settings.Amsat.Enabled)
      {
        listView1.SmallImageList = null;
        return;
      }

      listView1.SmallImageList = imageList1;
      var loader = ctx.AmsatStatusLoader;

      foreach (var item in Items)
      {
        int? norad_id = ((ItemData)item.Tag!).Sat.norad_cat_id;
        bool? status = norad_id == null ? null : loader.IsActive(norad_id.Value);

        if (status != null)
          item.ImageIndex = status.Value ? 1 : 2;
        else
          item.ImageIndex = -1;
      }
    }

    private string MakeAmsatTooltip(SatnogsDbSatellite sat)
    {
      if (sat.norad_cat_id == null ||
        !ctx.AmsatStatusLoader.Statuses.TryGetValue(sat.norad_cat_id.Value, out var statuses)) return "";

      var active = statuses.Where(s => s.Active).OrderByDescending(s => s.LatestTime).ToArray();
      if (active.Length == 0) return "No transmitters reported active";

      var now = DateTime.UtcNow;
      var lines = active.Select(s =>
        $"{s.Mode}:  {Utils.TimespanToString(now - s.LatestTime, false)} ago,  " +
        $"{s.ActiveCount} report{(s.ActiveCount == 1 ? "" : "s")}");

      return $"Reported active:\n{string.Join("\n", lines)}";
    }

    public void UpdatePassTimes()
    {
      if (Items == null || Items.Length == 0) return;

      var now = DateTime.UtcNow;
      bool changed = false;

      foreach (var item in Items)
      {
        if (item?.Tag is not ItemData data) continue;
        if (item.SubItems.Count < 4) continue;
        if (data.Pass == null || data.Pass.EndTime < now)
        {
          // re-sort only when the next-pass orbit number actually changes,
          // so satellites with no upcoming pass (Pass stays null) don't force a sort every second
          int? oldOrbit = data.Pass?.OrbitNumber;
          data.Pass = ctx.HamPasses.GetNextPass(data.Sat);
          if (data.Pass?.OrbitNumber != oldOrbit) changed = true;
        }

        if (data.Pass == null)
        {
          item.SubItems[2].Text = "n/a";
          item.SubItems[3].Text = "";
        }
        else if (data.Pass.StartTime < now)
        {
          item.SubItems[2].Text = "Now";
          item.SubItems[3].Text = $"{Math.Round(data.Pass.MaxElevation)}°";
        }
        else
        {
          item.SubItems[2].Text = $"{Utils.TimespanToString(data.Pass.StartTime - now)}";
          item.SubItems[3].Text = $"{Math.Round(data.Pass.MaxElevation)}°";
        }

        item.ToolTipText = $"{data.Sat.GetTooltipText()}\n\n{string.Join("\n", data.Pass?.GetTooltipText(false) ?? [])}";
      }

      // only auto-sort while the list is ordered by the Next Pass column
      if (changed && SortColumn == 2) SortItems();
      listView1.Invalidate();
    }

    private void SortItems()
    {
      switch (SortColumn)
      {
        case 0: Items = Items.OrderBy(item => item.Text).ToArray(); break;
        case 1: Items = Items.OrderBy(item => ((ItemData)item.Tag!).Sat.norad_cat_id).ToArray(); break;
        case 2: Items = Items.OrderBy(item => ((ItemData)item.Tag!).Pass?.StartTime ?? DateTime.MaxValue).ToArray(); break;
        case 3: Items = Items.OrderBy(item => ((ItemData)item.Tag!).Pass?.MaxElevation).ToArray(); break;
      }

      listView1.Invalidate();
    }




    //----------------------------------------------------------------------------------------------
    //                                        events
    //----------------------------------------------------------------------------------------------
    private void listView1_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
    {
      if (Items == null || Items.Length == 0)
      {
        e.Item = new ListViewItem("");
        return;
      }

      int idx = Math.Clamp(e.ItemIndex, 0, Items.Length - 1);
      e.Item = Items[idx];
    }

    private void listView1_ColumnClick(object sender, ColumnClickEventArgs e)
    {
      SortColumn = e.Column;
      SortItems();
    }

    private void listView1_SelectedIndexChanged(object sender, EventArgs e)
    {
      if (MouseButtons == MouseButtons.Right) return;
      if (!TryGetSelectedItem(out var data)) return;

      ctx.SatelliteSelector.SetSelectedSatellite(data.Sat);
      ctx.PassesPanel?.ShowPasses();
      if (data.Pass != null) ctx.SatelliteSelector.SetSelectedPass(data.Pass);
    }

    private void listView1_MouseDown(object sender, MouseEventArgs e)
    {
      if (e.Button != MouseButtons.Right) return;

      var item = listView1.GetItemAt(e.X, e.Y);
      if (item == null) return;

      listView1.SelectedIndices.Clear();
      item.Selected = true;
    }

    private void SatelliteDetailsMNU_Click(object sender, EventArgs e)
    {
      // use the sat captured when the menu opened: a timer-driven list rebuild
      // (UpdatePassTimes -> SortItems) can clear SelectedIndices while the menu is open
      if (ContextMenuSat == null) return;
      SatelliteDetailsForm.ShowSatellite(ContextMenuSat, ctx.MainForm, ctx);
    }

    private void MonitorSatelliteMNU_Click(object sender, EventArgs e)
    {
      if (ContextMenuSat == null) return;
      ToggleMonitored(ContextMenuSat);
    }

    private void ToggleMonitored(SatnogsDbSatellite sat)
    {
      string? txId = ctx.SatelliteSelector.SelectedTransmitter?.uuid
        ?? sat.Transmitters.FirstOrDefault()?.uuid;
      ctx.MonitoredSatellites.ToggleEntry(sat, txId);
      ctx.MonitoredPasses?.FullRebuild();
      ctx.MonitoredSatellitesPanel?.RefreshList();
    }

    private void contextMenuStrip1_Opening(object sender, System.ComponentModel.CancelEventArgs e)
    {
      if (listView1.SelectedIndices.Count == 0)
      {
        e.Cancel = true;
        ContextMenuSat = null;
        return;
      }

      ContextMenuSat = ((ItemData)Items[listView1.SelectedIndices[0]].Tag!).Sat;
      bool monitored = ctx.MonitoredSatellites.Contains(ContextMenuSat.sat_id);
      MonitorSatelliteMNU.Text = monitored ? "Unmonitor Satellite" : "Monitor Satellite";

    }

    private void listView1_MouseMove(object? sender, MouseEventArgs e)
    {
      // not HitTest(): in Details view it throws when the sub-item hit test returns -1
      // (point on the row but outside all columns)
      var item = listView1.GetItemAt(e.X, e.Y);
      if (item != null && (item.ImageIndex < 0 || !item.GetBounds(ItemBoundsPortion.Icon).Contains(e.Location))) item = null;
      if (item == AmsatToolTipItem) return;

      HideAmsatToolTip();
      if (item == null) return;

      // show after the usual hover delay
      AmsatToolTipItem = item;
      listView1.EnableItemToolTips(false);
      AmsatToolTipTimer?.Start();
    }

    private void listView1_MouseLeave(object? sender, EventArgs e)
    {
      HideAmsatToolTip();
    }

    private void AmsatToolTipTimer_Tick(object? sender, EventArgs e)
    {
      AmsatToolTipTimer!.Stop();
      if (AmsatToolTipItem == null) return;

      string text = MakeAmsatTooltip(((ItemData)AmsatToolTipItem.Tag!).Sat);
      if (text == "") return;

      var point = PointToClient(Cursor.Position);
      point.Offset(0, 20);
      AmsatToolTip!.Show(text, this, point);
    }

    private void HideAmsatToolTip()
    {
      AmsatToolTipTimer?.Stop();
      if (AmsatToolTipItem == null) return;

      AmsatToolTipItem = null;
      AmsatToolTip?.Hide(this);
      listView1.EnableItemToolTips(true);
    }
  }
}