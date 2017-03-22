﻿
using System;
using System.Windows.Forms;
using Intersect_Editor.Classes;
using Intersect_Library;
using Intersect_Library.GameObjects.Events;
using Intersect_Library.Localization;

namespace Intersect_Editor.Forms.Editors.Event_Commands
{
    public partial class EventCommand_ChangeItems : UserControl
    {
        private EventCommand _myCommand;
        private EventPage _currentPage;
        private readonly FrmEvent _eventEditor;
        public EventCommand_ChangeItems(EventCommand refCommand, EventPage refPage, FrmEvent editor)
        {
            InitializeComponent();
            _myCommand = refCommand;
            _eventEditor = editor;
            _currentPage = refPage;
            InitLocalization();
            cmbItem.Items.Clear();
            cmbItem.Items.AddRange(Database.GetGameObjectList(GameObject.Item));
            cmbAction.SelectedIndex = _myCommand.Ints[0];
            cmbItem.SelectedIndex = Database.GameObjectListIndex(GameObject.Item,_myCommand.Ints[1]);
            if (_myCommand.Ints[2] < 1)
            {
                nudGiveTakeAmount.Value = 1;
            }
            else
            {
                nudGiveTakeAmount.Value = _myCommand.Ints[2];
            }
            lblAmount.Text = Strings.Get("eventchangeitems","amount");
        }

        private void InitLocalization()
        {
            grpChangeItems.Text = Strings.Get("eventchangeitems", "title");
            lblAction.Text = Strings.Get("eventchangeitems", "action");
            cmbAction.Items.Clear();
            for (int i = 0; i < 2; i++)
            {
                cmbAction.Items.Add(Strings.Get("eventchangeitems", "action" + i));
            }
            btnSave.Text = Strings.Get("eventchangeitems", "okay");
            btnCancel.Text = Strings.Get("eventchangeitems", "cancel");
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            _myCommand.Ints[0] = cmbAction.SelectedIndex;
            _myCommand.Ints[1] = Database.GameObjectIdFromList(GameObject.Item,cmbItem.SelectedIndex);
            _myCommand.Ints[2] = (int)nudGiveTakeAmount.Value;
            if (_myCommand.Ints[4] == 0)
            // command.Ints[4, and 5] are reserved for when the action succeeds or fails
            {
                for (var i = 0; i < 2; i++)
                {
                    _currentPage.CommandLists.Add(new CommandList());
                    _myCommand.Ints[4 + i] = _currentPage.CommandLists.Count - 1;
                }
            }
            _eventEditor.FinishCommandEdit();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            _eventEditor.CancelCommandEdit();
        }
    }
}
