using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace GraphProcessor
{
    public class ExposedParameterFieldView : BlackboardField
    {
        #region Fields
        protected BaseGraphView graphView;
        #endregion

        #region Properties
        public ExposedParameter parameter { get; }

        public bool IsEditing { get; private set; }
        #endregion

        #region Constructors
        public ExposedParameterFieldView(BaseGraphView graphView, ExposedParameter param) : base(null, param.name, param.ShortType)
        {
            this.graphView = graphView;
            parameter = param;
            this.AddManipulator(new ContextualMenuManipulator(BuildContextualMenu));
            this.Q("icon").AddToClassList("parameter-" + param.ShortType);
            this.Q("icon").visible = true;

            var textField = this.Q("textField") as TextField;

            // Register for when editing starts (focus gained)
            textField.RegisterCallback<FocusInEvent>(e => { IsEditing = true; });

            // Register for when editing is finished (focus lost)
            textField.RegisterCallback<FocusOutEvent>(e =>
            {
                IsEditing = false;
                if (textField.value != param.name)
                {
                    text = textField.value;
                    graphView.graph.UpdateExposedParameterName(param, textField.value);
                }
            });

            // Register for Enter key press to finish editing
            textField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    IsEditing = false;
                    if (textField.value != param.name)
                    {
                        text = textField.value;
                        graphView.graph.UpdateExposedParameterName(param, textField.value);
                    }
                    textField.Blur(); // Remove focus
                    e.StopPropagation();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    IsEditing = false;
                    textField.SetValueWithoutNotify(param.name); // Revert to original name
                    textField.Blur(); // Remove focus
                    e.StopPropagation();
                }
            });
        }
        #endregion

        #region Methods
        private void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            evt.menu.AppendAction("Rename", a => StartEditing(), DropdownMenuAction.AlwaysEnabled);
            evt.menu.AppendAction("Delete", a => graphView.graph.RemoveExposedParameter(parameter), DropdownMenuAction.AlwaysEnabled);

            evt.StopPropagation();
        }

        public void StartEditing()
        {
            if (IsEditing)
            {
                return;
            }
            OpenTextEditor();
        }
        #endregion
    }
}