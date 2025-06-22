using System;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace GraphProcessor
{
    public abstract class PinnedElementView : GraphElement
    {
        #region Fields
        private static readonly string pinnedElementStyle = "GraphProcessorStyles/PinnedElementView";
        private static readonly string pinnedElementTree = "GraphProcessorElements/PinnedElement";
        protected PinnedElement _pinnedElement;
        protected VisualElement _root;
        protected VisualElement _content;
        protected VisualElement _header;

        private readonly VisualElement _main;
        private readonly Label _titleLabel;
        private bool _scrollable;
        private readonly ScrollView _scrollView;
        #endregion

        #region Properties
        public override string title
        {
            get => _titleLabel.text;
            set => _titleLabel.text = value;
        }

        protected bool scrollable
        {
            get => _scrollable;
            set
            {
                if (_scrollable == value)
                {
                    return;
                }

                _scrollable = value;

                style.position = Position.Absolute;
                if (_scrollable)
                {
                    _content.RemoveFromHierarchy();
                    _root.Add(_scrollView);
                    _scrollView.Add(_content);
                    AddToClassList("scrollable");
                }
                else
                {
                    _scrollView.RemoveFromHierarchy();
                    _content.RemoveFromHierarchy();
                    _root.Add(_content);
                    RemoveFromClassList("scrollable");
                }
            }
        }
        #endregion

        #region Constructors
        public PinnedElementView()
        {
            var tpl = Resources.Load<VisualTreeAsset>(pinnedElementTree);
            styleSheets.Add(Resources.Load<StyleSheet>(pinnedElementStyle));

            _main = tpl.CloneTree();
            _main.AddToClassList("mainContainer");
            _scrollView = new ScrollView(ScrollViewMode.VerticalAndHorizontal);

            _root = _main.Q("content");

            _header = _main.Q("header");

            _titleLabel = _main.Q<Label>("titleLabel");
            _content = _main.Q<VisualElement>("contentContainer");

            hierarchy.Add(_main);

            capabilities |= Capabilities.Movable | Capabilities.Resizable;
            style.overflow = Overflow.Hidden;

            ClearClassList();
            AddToClassList("pinnedElement");

            this.AddManipulator(new Dragger { clampToParentEdges = true });

            scrollable = false;

            hierarchy.Add(new Resizer(() => onResized?.Invoke()));

            RegisterCallback<DragUpdatedEvent>(
                e =>
                {
                    e.StopPropagation();
                });

            title = "PinnedElementView";
        }
        #endregion

        #region Methods
        protected event Action onResized;

        public void InitializeGraphView(PinnedElement pinnedElement, BaseGraphView graphView)
        {
            _pinnedElement = pinnedElement;

            // Clamp position and size to window bounds
            var windowSize = graphView.layout.size;
            var clampedPosition = new Rect(
                Mathf.Clamp(pinnedElement.position.x, 0, windowSize.x - pinnedElement.position.width),
                Mathf.Clamp(pinnedElement.position.y, 0, windowSize.y - pinnedElement.position.height),
                Mathf.Clamp(pinnedElement.position.width, 50, windowSize.x),
                Mathf.Clamp(pinnedElement.position.height, 50, windowSize.y)
            );
            SetPosition(clampedPosition);

            onResized += () =>
            {
                var newSize = layout.size;
                var newPosition = layout.position;
                var clampedRect = new Rect(
                    Mathf.Clamp(newPosition.x, 0, windowSize.x - newSize.x),
                    Mathf.Clamp(newPosition.y, 0, windowSize.y - newSize.y),
                    Mathf.Clamp(newSize.x, 50, windowSize.x),
                    Mathf.Clamp(newSize.y, 50, windowSize.y)
                );
                pinnedElement.position = clampedRect;
            };

            RegisterCallback<MouseUpEvent>(
                e =>
                {
                    var newPosition = layout.position;
                    var clampedPosition = new Vector2(
                        Mathf.Clamp(newPosition.x, 0, windowSize.x - layout.width),
                        Mathf.Clamp(newPosition.y, 0, windowSize.y - layout.height)
                    );
                    pinnedElement.position.position = clampedPosition;
                });

            Initialize(graphView);
        }

        public void ResetPosition()
        {
            _pinnedElement.position = new Rect(_pinnedElement.DefaultPosition, _pinnedElement.DefaultSize);
            SetPosition(_pinnedElement.position);
        }

        protected abstract void Initialize(BaseGraphView graphView);

        ~PinnedElementView()
        {
            Destroy();
        }

        protected virtual void Destroy() { }
        #endregion
    }
}