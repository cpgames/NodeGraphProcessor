using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace GraphProcessor
{
    public class NodeBadgeView : IconBadge
    {
        #region Fields
        private Label label;
        private Texture icon;
        private Color color;
        private bool isCustom;
        #endregion

        #region Constructors
        public NodeBadgeView(string message, NodeMessageType messageType)
        {
            switch (messageType)
            {
                case NodeMessageType.Warning:
                    CreateCustom(message, EditorGUIUtility.IconContent("Collab.Warning").image, Color.yellow);
                    break;
                case NodeMessageType.Error:
                    CreateCustom(message, EditorGUIUtility.IconContent("Collab.Warning").image, Color.red);
                    break;
                case NodeMessageType.Info:
                    CreateCustom(message, EditorGUIUtility.IconContent("console.infoicon").image, Color.white);
                    break;
                default:
                case NodeMessageType.None:
                    CreateCustom(message, null, Color.grey);
                    break;
            }
        }

        public NodeBadgeView(string message, Texture icon, Color color)
        {
            CreateCustom(message, icon, color);
        }
        #endregion

        #region Methods
        private void CreateCustom(string message, Texture icon, Color color)
        {
            badgeText = message;
            this.color = color;

            var image = this.Q<Image>("icon");
            image.image = icon;
            image.style.backgroundColor = color;
            style.color = color;
            // This will set a class name containing the hash code of the string
            // We use this little trick to retrieve the label once it is added to the graph
            visualStyle = badgeText.GetHashCode().ToString();
        }

        protected override void HandleEventBubbleUp(EventBase evt)
        {
            base.HandleEventBubbleUp(evt);

            if (evt.eventTypeId == MouseEnterEvent.TypeId())
            {
                // And then we can fetch it here:
                var gv = GetFirstAncestorOfType<GraphView>();
                var label = gv.Q<Label>(classes: new[] { "icon-badge__text--" + badgeText.GetHashCode() });
                if (label != null)
                {
                    label.style.color = color;
                }
            }
        }
        #endregion
    }
}