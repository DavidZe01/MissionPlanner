using MissionPlanner.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    public partial class ConfigGPSOrder : MyUserControl, IActivate
    {
        public ConfigGPSOrder()
        {
            InitializeComponent();
        }

        public void Activate()
        {
            if (!MainV2.comPort.MAV.param.ContainsKey("GPS1_CAN_OVRIDE"))
            {
                this.Enabled = false;
                return;
            }

            // a parameter missing from this firmware reads as 0 (current firmware names the
            // detected ids GPS1_CAN_NODEID/GPS2_CAN_NODEID, older GPS_CAN_NODEID1/2)
            double Param(params string[] names)
            {
                foreach (var name in names)
                    if (MainV2.comPort.MAV.param.ContainsKey(name) && MainV2.comPort.MAV.param[name] != null)
                        return MainV2.comPort.MAV.param[name].Value;
                return 0;
            }

            // get the detected id's
            var id1 = new { Value = Param("GPS_CAN_NODEID1", "GPS1_CAN_NODEID") };
            var id2 = new { Value = Param("GPS_CAN_NODEID2", "GPS2_CAN_NODEID") };

            // get the override id's
            var id1ovr = new { Value = Param("GPS1_CAN_OVRIDE") };
            var id2ovr = new { Value = Param("GPS2_CAN_OVRIDE") };

            var list = new List<GPSCAN>();

            if (id1ovr.Value != 0)
                list.Add(new GPSCAN() {Order = 1, NodeID = (int) id1ovr.Value, Name = "GPS Override 1"});
            if (id2ovr.Value != 0)
                list.Add(new GPSCAN() {Order = 2, NodeID = (int) id2ovr.Value, Name = "GPS Override 2"});
            if (id1.Value != 0 && id1.Value != id1ovr.Value && id1.Value != id2ovr.Value)
                list.Add(new GPSCAN() {Order = 98, NodeID = (int) id1.Value, Name = "GPS Detect 1"});
            if (id2.Value != 0 && id2.Value != id1ovr.Value && id2.Value != id2ovr.Value)
                list.Add(new GPSCAN() {Order = 99, NodeID = (int) id2.Value, Name = "GPS Detect 2"});

            var bs = new BindingSource();
            bs.DataSource = list;
            myDataGridView1.DataSource = bs;
        }

        public class GPSCAN
        {
            public int Order { get; set; }

            public string Name { get; set; } = "GPS";

            public int NodeID { get; set; }
        }

        private void myDataGridView1_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            try
            {
                if (e.ColumnIndex == GPS1.Index)
                {
                    MainV2.comPort.setParam(MainV2.comPort.sysidcurrent, (byte) MainV2.comPort.compidcurrent,
                        "GPS1_CAN_OVRIDE",
                        int.Parse(myDataGridView1[nodeIDDataGridViewTextBoxColumn.Index, e.RowIndex].Value.ToString()));
                    Activate();
                }

                if (e.ColumnIndex == GPS2.Index)
                {
                    MainV2.comPort.setParam(MainV2.comPort.sysidcurrent, (byte) MainV2.comPort.compidcurrent,
                        "GPS2_CAN_OVRIDE",
                        int.Parse(myDataGridView1[nodeIDDataGridViewTextBoxColumn.Index, e.RowIndex].Value.ToString()));
                    Activate();
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(Strings.ERROR, "Failed to set param " + ex.ToString());
            }
        }
    }
}
