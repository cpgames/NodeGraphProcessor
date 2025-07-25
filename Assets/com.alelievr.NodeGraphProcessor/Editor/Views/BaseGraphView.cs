﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Status = UnityEngine.UIElements.DropdownMenuAction.Status;
using Object = UnityEngine.Object;

namespace GraphProcessor
{
    /// <summary>
    ///     Base class to write a custom view for a node
    /// </summary>
    public class BaseGraphView : GraphView, IDisposable
    {
        #region Nested type: ComputeOrderUpdatedDelegate
        public delegate void ComputeOrderUpdatedDelegate();
        #endregion

        #region Nested type: NodeDuplicatedDelegate
        public delegate void NodeDuplicatedDelegate(BaseNode duplicatedNode, BaseNode newNode);
        #endregion

        #region Fields
        /// <summary>
        ///     Graph that owns of the node
        /// </summary>
        public BaseGraph graph;

        /// <summary>
        ///     Connector listener that will create the edges between ports
        /// </summary>
        public BaseEdgeConnectorListener connectorListener;

        /// <summary>
        ///     List of all node views in the graph
        /// </summary>
        /// <typeparam name="BaseNodeView"></typeparam>
        /// <returns></returns>
        public readonly List<BaseNodeView> nodeViews = new();

        /// <summary>
        ///     Dictionary of the node views accessed view the node instance, faster than a Find in the node view list
        /// </summary>
        /// <typeparam name="BaseNode"></typeparam>
        /// <typeparam name="BaseNodeView"></typeparam>
        /// <returns></returns>
        public readonly Dictionary<BaseNode, BaseNodeView> nodeViewsPerNode = new();

        /// <summary>
        ///     List of all edge views in the graph
        /// </summary>
        /// <typeparam name="EdgeView"></typeparam>
        /// <returns></returns>
        public readonly List<EdgeView> edgeViews = new();

        /// <summary>
        ///     List of all group views in the graph
        /// </summary>
        /// <typeparam name="GroupView"></typeparam>
        /// <returns></returns>
        public readonly List<GroupView> groupViews = new();

#if UNITY_2020_1_OR_NEWER
        /// <summary>
        ///     List of all sticky note views in the graph
        /// </summary>
        /// <typeparam name="StickyNoteView"></typeparam>
        /// <returns></returns>
        public readonly List<StickyNoteView> stickyNoteViews = new();
#endif

        /// <summary>
        ///     List of all stack node views in the graph
        /// </summary>
        /// <typeparam name="BaseStackNodeView"></typeparam>
        /// <returns></returns>
        public readonly List<BaseStackNodeView> stackNodeViews = new();

        private readonly Dictionary<Type, PinnedElementView> pinnedElements = new();

        private readonly CreateNodeMenuWindow createNodeMenu;

        private readonly Dictionary<Type, (Type nodeType, MethodInfo initalizeNodeFromObject)> nodeTypePerCreateAssetType = new();
        #endregion

        #region Properties
        /// <summary>
        ///     Object to handle nodes that shows their UI in the inspector.
        /// </summary>
        protected NodeInspectorObject nodeInspector
        {
            get
            {
                if (graph.nodeInspectorReference == null)
                {
                    graph.nodeInspectorReference = CreateNodeInspectorObject();
                }
                return graph.nodeInspectorReference as NodeInspectorObject;
            }
        }

        /// <summary>
        ///     Workaround object for creating exposed parameter property fields.
        /// </summary>
        public ExposedParameterFieldFactory exposedParameterFactory { get; private set; }

        public SerializedObject serializedGraph { get; private set; }
        #endregion

        #region Constructors
        public BaseGraphView(EditorWindow window)
        {
            serializeGraphElements = SerializeGraphElementsCallback;
            canPasteSerializedData = CanPasteSerializedDataCallback;
            unserializeAndPaste = DeserializeAndPasteCallback;
            graphViewChanged = GraphViewChangedCallback;
            viewTransformChanged = ViewTransformChangedCallback;
            elementResized = ElementResizedCallback;

            RegisterCallback<KeyDownEvent>(KeyDownCallback);
            RegisterCallback<DragPerformEvent>(DragPerformedCallback);
            RegisterCallback<DragUpdatedEvent>(DragUpdatedCallback);
            RegisterCallback<MouseDownEvent>(MouseDownCallback);
            RegisterCallback<MouseUpEvent>(MouseUpCallback);

            InitializeManipulators();

            SetupZoom(0.05f, 2f);

            Undo.undoRedoPerformed += ReloadView;

            createNodeMenu = ScriptableObject.CreateInstance<CreateNodeMenuWindow>();
            createNodeMenu.Initialize(this, window);

            this.StretchToParentSize();
        }
        #endregion

        #region Methods
        protected virtual NodeInspectorObject CreateNodeInspectorObject()
        {
            var inspector = ScriptableObject.CreateInstance<NodeInspectorObject>();
            inspector.name = "Node Inspector";
            inspector.hideFlags = HideFlags.HideAndDontSave ^ HideFlags.NotEditable;

            return inspector;
        }
        #endregion

        /// <summary>
        ///     Triggered just after the graph is initialized
        /// </summary>
        public event Action initialized;

        /// <summary>
        ///     Triggered just after the compute order of the graph is updated
        /// </summary>
        public event ComputeOrderUpdatedDelegate computeOrderUpdated;

        // Safe event relay from BaseGraph (safe because you are sure to always point on a valid BaseGraph
        // when one of these events is called), a graph switch can occur between two call tho
        /// <summary>
        ///     Same event than BaseGraph.onExposedParameterListChanged
        ///     Safe event (not triggered in case the graph is null).
        /// </summary>
        public event Action onExposedParameterListChanged;

        /// <summary>
        ///     Same event than BaseGraph.onExposedParameterModified
        ///     Safe event (not triggered in case the graph is null).
        /// </summary>
        public event Action<ExposedParameter> onExposedParameterModified;

        /// <summary>
        ///     Triggered when a node is duplicated (crt-d) or copy-pasted (crtl-c/crtl-v)
        /// </summary>
        public event NodeDuplicatedDelegate nodeDuplicated;

        #region Callbacks
        protected override bool canCopySelection
        {
            get { return selection.Any(e => e is BaseNodeView || e is GroupView); }
        }

        protected override bool canCutSelection
        {
            get { return selection.Any(e => e is BaseNodeView || e is GroupView); }
        }

        private string SerializeGraphElementsCallback(IEnumerable<GraphElement> elements)
        {
            var data = new CopyPasteHelper();

            foreach (var nodeView in elements.OfType<BaseNodeView>())
            {
                data.copiedNodes.Add(JsonSerializer.SerializeNode(nodeView.nodeTarget));
                foreach (var port in nodeView.nodeTarget.GetAllPorts())
                {
                    if (port.portData.vertical)
                    {
                        foreach (var edge in port.GetEdges())
                        {
                            data.copiedEdges.Add(JsonSerializer.Serialize(edge));
                        }
                    }
                }
            }

            foreach (var groupView in elements.OfType<GroupView>())
            {
                data.copiedGroups.Add(JsonSerializer.Serialize(groupView.group));
            }

            foreach (var edgeView in elements.OfType<EdgeView>())
            {
                data.copiedEdges.Add(JsonSerializer.Serialize(edgeView.serializedEdge));
            }

            ClearSelection();

            return JsonUtility.ToJson(data, true);
        }

        private bool CanPasteSerializedDataCallback(string serializedData)
        {
            try
            {
                return JsonUtility.FromJson(serializedData, typeof(CopyPasteHelper)) != null;
            }
            catch
            {
                return false;
            }
        }

        private void DeserializeAndPasteCallback(string operationName, string serializedData)
        {
            var data = JsonUtility.FromJson<CopyPasteHelper>(serializedData);

            RegisterCompleteObjectUndo(operationName);

            var copiedNodesMap = new Dictionary<string, BaseNode>();

            var unserializedGroups = data.copiedGroups.Select(g => JsonSerializer.Deserialize<Group>(g)).ToList();

            foreach (var serializedNode in data.copiedNodes)
            {
                var node = JsonSerializer.DeserializeNode(serializedNode);

                if (node == null)
                {
                    continue;
                }

                var sourceGUID = node.GUID;
                graph.nodesPerGUID.TryGetValue(sourceGUID, out var sourceNode);
                //Call OnNodeCreated on the new fresh copied node
                node.createdFromDuplication = true;
                node.createdWithinGroup = unserializedGroups.Any(g => g.innerNodeGUIDs.Contains(sourceGUID));
                node.OnNodeCreated();
                //And move a bit the new node
                node.position.position += new Vector2(20, 20);

                var newNodeView = AddNode(node);

                // If the nodes were copied from another graph, then the source is null
                if (sourceNode != null)
                {
                    nodeDuplicated?.Invoke(sourceNode, node);
                }
                copiedNodesMap[sourceGUID] = node;

                //Select the new node
                AddToSelection(nodeViewsPerNode[node]);
            }

            foreach (var group in unserializedGroups)
            {
                //Same than for node
                group.OnCreated();

                // try to centre the created node in the screen
                group.position.position += new Vector2(20, 20);

                var oldGUIDList = group.innerNodeGUIDs.ToList();
                group.innerNodeGUIDs.Clear();
                foreach (var guid in oldGUIDList)
                {
                    graph.nodesPerGUID.TryGetValue(guid, out var node);

                    // In case group was copied from another graph
                    if (node == null)
                    {
                        copiedNodesMap.TryGetValue(guid, out node);
                        group.innerNodeGUIDs.Add(node.GUID);
                    }
                    else
                    {
                        group.innerNodeGUIDs.Add(copiedNodesMap[guid].GUID);
                    }
                }

                AddGroup(group);
            }

            foreach (var serializedEdge in data.copiedEdges)
            {
                var edge = JsonSerializer.Deserialize<SerializableEdge>(serializedEdge);
                edge.Deserialize(graph);

                // Find port of new nodes:
                copiedNodesMap.TryGetValue(edge._inputNode.GUID, out var oldInputNode);
                copiedNodesMap.TryGetValue(edge._outputNode.GUID, out var oldOutputNode);

                // We avoid to break the graph by replacing unique connections:
                if ((oldInputNode == null && !edge._inputPort.portData.acceptMultipleEdges) || !edge._outputPort.portData.acceptMultipleEdges)
                {
                    continue;
                }

                oldInputNode ??= edge._inputNode;
                oldOutputNode ??= edge._outputNode;

                var inputPort = oldInputNode.GetPort(edge._inputPort.fieldName, edge.inputPortIdentifier);
                var outputPort = oldOutputNode.GetPort(edge._outputPort.fieldName, edge.outputPortIdentifier);

                var newEdge = SerializableEdge.CreateNewEdge(graph, inputPort, outputPort);

                if (nodeViewsPerNode.ContainsKey(oldInputNode) && nodeViewsPerNode.ContainsKey(oldOutputNode))
                {
                    var edgeView = CreateEdgeView();
                    edgeView.userData = newEdge;
                    edgeView.input = nodeViewsPerNode[oldInputNode].GetPortViewFromFieldName(newEdge.inputFieldName, newEdge.inputPortIdentifier);
                    edgeView.output = nodeViewsPerNode[oldOutputNode].GetPortViewFromFieldName(newEdge.outputFieldName, newEdge.outputPortIdentifier);

                    Connect(edgeView);
                }
            }
        }

        public virtual EdgeView CreateEdgeView()
        {
            return new EdgeView();
        }

        private GraphViewChange GraphViewChangedCallback(GraphViewChange changes)
        {
            if (changes.elementsToRemove != null)
            {
                RegisterCompleteObjectUndo("Remove Graph Elements");

                // Destroy priority of objects
                // We need nodes to be destroyed first because we can have a destroy operation that uses node connections
                changes.elementsToRemove.Sort((e1, e2) =>
                {
                    int GetPriority(GraphElement e)
                    {
                        if (e is BaseNodeView)
                        {
                            return 0;
                        }
                        return 1;
                    }

                    return GetPriority(e1).CompareTo(GetPriority(e2));
                });

                //Handle ourselves the edge and node remove
                changes.elementsToRemove.RemoveAll(e =>
                {
                    switch (e)
                    {
                        case EdgeView edge:
                            Disconnect(edge);
                            return true;
                        case BaseNodeView nodeView:
                            // For vertical nodes, we need to delete them ourselves as it's not handled by GraphView
                            foreach (var pv in nodeView.inputPortViews.Concat(nodeView.outputPortViews))
                            {
                                if (pv.orientation == Orientation.Vertical)
                                {
                                    foreach (var edge in pv.GetEdges().ToList())
                                    {
                                        Disconnect(edge);
                                    }
                                }
                            }

                            nodeInspector.NodeViewRemoved(nodeView);
                            ExceptionToLog.Call(() => nodeView.OnRemoved());
                            graph.RemoveNode(nodeView.nodeTarget);
                            UpdateSerializedProperties();
                            RemoveElement(nodeView);
                            if (Selection.activeObject == nodeInspector)
                            {
                                UpdateNodeInspectorSelection();
                            }

                            SyncSerializedPropertyPathes();
                            return true;
                        case GroupView group:
                            graph.RemoveGroup(group.group);
                            UpdateSerializedProperties();
                            RemoveElement(group);
                            return true;
                        case ExposedParameterFieldView blackboardField:
                            graph.RemoveExposedParameter(blackboardField.parameter);
                            UpdateSerializedProperties();
                            return true;
                        case BaseStackNodeView stackNodeView:
                            graph.RemoveStackNode(stackNodeView.stackNode);
                            UpdateSerializedProperties();
                            RemoveElement(stackNodeView);
                            return true;
#if UNITY_2020_1_OR_NEWER
                        case StickyNoteView stickyNoteView:
                            graph.RemoveStickyNote(stickyNoteView.note);
                            UpdateSerializedProperties();
                            RemoveElement(stickyNoteView);
                            return true;
#endif
                    }

                    return false;
                });
            }

            return changes;
        }

        private void GraphChangesCallback(GraphChanges changes)
        {
            if (changes.removedEdge != null)
            {
                var edge = edgeViews.FirstOrDefault(e => e.serializedEdge == changes.removedEdge);

                DisconnectView(edge);
            }
        }

        private void ViewTransformChangedCallback(GraphView view)
        {
            if (graph != null)
            {
                graph.position = viewTransform.position;
                graph.scale = viewTransform.scale;
            }
        }

        private void ElementResizedCallback(VisualElement elem)
        {
            if (elem is GroupView groupView)
            {
                groupView.group.size = groupView.GetPosition().size;
            }
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            var compatiblePorts = new List<Port>();

            compatiblePorts.AddRange(
                ports.ToList().Where(p =>
                {
                    var portView = p as PortView;

                    if (portView.owner == (startPort as PortView).owner)
                    {
                        return false;
                    }

                    if (p.direction == startPort.direction)
                    {
                        return false;
                    }

                    //Check for type assignability
                    if (!BaseGraph.TypesAreConnectable(startPort.portType, p.portType))
                    {
                        return false;
                    }

                    //Check if the edge already exists
                    if (portView.GetEdges().Any(e => e.input == startPort || e.output == startPort))
                    {
                        return false;
                    }

                    return true;
                }));

            return compatiblePorts;
        }

        /// <summary>
        ///     Build the contextual menu shown when right clicking inside the graph view
        /// </summary>
        /// <param name="evt"></param>
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            base.BuildContextualMenu(evt);
            BuildGroupContextualMenu(evt, 1);
            BuildStickyNoteContextualMenu(evt, 2);
            BuildViewContextualMenu(evt);
            BuildSelectAssetContextualMenu(evt);
            BuildSaveAssetContextualMenu(evt);
            BuildHelpContextualMenu(evt);
        }

        /// <summary>
        ///     Add the New Group entry to the context menu
        /// </summary>
        /// <param name="evt"></param>
        protected virtual void BuildGroupContextualMenu(ContextualMenuPopulateEvent evt, int menuPosition = -1)
        {
            if (menuPosition == -1)
            {
                menuPosition = evt.menu.MenuItems().Count;
            }
            var position = (evt.currentTarget as VisualElement).ChangeCoordinatesTo(contentViewContainer, evt.localMousePosition);
            evt.menu.InsertAction(
                menuPosition,
                "Create Group",
                e => AddSelectionsToGroup(AddGroup(new Group("Create Group", position))),
                DropdownMenuAction.AlwaysEnabled);
        }

        /// <summary>
        ///     -Add the New Sticky Note entry to the context menu
        /// </summary>
        /// <param name="evt"></param>
        protected virtual void BuildStickyNoteContextualMenu(ContextualMenuPopulateEvent evt, int menuPosition = -1)
        {
            if (menuPosition == -1)
            {
                menuPosition = evt.menu.MenuItems().Count;
            }
#if UNITY_2020_1_OR_NEWER
            var position = (evt.currentTarget as VisualElement).ChangeCoordinatesTo(contentViewContainer, evt.localMousePosition);
            evt.menu.InsertAction(
                menuPosition,
                "Create Sticky Note",
                e => AddStickyNote(new StickyNote("Create Note", position)),
                DropdownMenuAction.AlwaysEnabled);
#endif
        }

        /// <summary>
        ///     Add the View entry to the context menu
        /// </summary>
        /// <param name="evt"></param>
        protected virtual void BuildViewContextualMenu(ContextualMenuPopulateEvent evt)
        {
            evt.menu.AppendAction("View/Processor", e => ToggleView<ProcessorView, PinnedElement>(), e => GetPinnedElementStatus<ProcessorView>());
        }

        /// <summary>
        ///     Add the Select Asset entry to the context menu
        /// </summary>
        /// <param name="evt"></param>
        protected virtual void BuildSelectAssetContextualMenu(ContextualMenuPopulateEvent evt)
        {
            evt.menu.AppendAction("Select Asset", e => EditorGUIUtility.PingObject(graph), DropdownMenuAction.AlwaysEnabled);
        }

        /// <summary>
        ///     Add the Save Asset entry to the context menu
        /// </summary>
        /// <param name="evt"></param>
        protected virtual void BuildSaveAssetContextualMenu(ContextualMenuPopulateEvent evt)
        {
            evt.menu.AppendAction(
                "Save Asset",
                e => { SaveGraphToDisk(); },
                DropdownMenuAction.AlwaysEnabled);
        }

        /// <summary>
        ///     Add the Help entry to the context menu
        /// </summary>
        /// <param name="evt"></param>
        protected void BuildHelpContextualMenu(ContextualMenuPopulateEvent evt)
        {
            evt.menu.AppendAction(
                "Help/Reset Pinned Windows",
                e =>
                {
                    foreach (var kp in pinnedElements)
                    {
                        kp.Value.ResetPosition();
                    }
                });
        }

        protected virtual void KeyDownCallback(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.S && e.commandKey)
            {
                SaveGraphToDisk();
                e.StopPropagation();
            }
            else if (e.keyCode == KeyCode.F2)
            {
                // Handle F2 for parameter renaming
                var selectedParameters = selection.OfType<ExposedParameterFieldView>().ToList();
                if (selectedParameters.Count == 1 && !selectedParameters[0].IsEditing)
                {
                    selectedParameters[0].StartEditing();
                    e.StopPropagation();
                }
            }
            else if (nodeViews.Count > 0 && e.commandKey && e.altKey)
            {
                //	Node Aligning shortcuts
                switch (e.keyCode)
                {
                    case KeyCode.LeftArrow:
                        nodeViews[0].AlignToLeft();
                        e.StopPropagation();
                        break;
                    case KeyCode.RightArrow:
                        nodeViews[0].AlignToRight();
                        e.StopPropagation();
                        break;
                    case KeyCode.UpArrow:
                        nodeViews[0].AlignToTop();
                        e.StopPropagation();
                        break;
                    case KeyCode.DownArrow:
                        nodeViews[0].AlignToBottom();
                        e.StopPropagation();
                        break;
                    case KeyCode.C:
                        nodeViews[0].AlignToCenter();
                        e.StopPropagation();
                        break;
                    case KeyCode.M:
                        nodeViews[0].AlignToMiddle();
                        e.StopPropagation();
                        break;
                }
            }
        }

        private void MouseUpCallback(MouseUpEvent e)
        {
            schedule.Execute(() =>
            {
                if (DoesSelectionContainsInspectorNodes())
                {
                    UpdateNodeInspectorSelection();
                }
            }).ExecuteLater(1);
        }

        private void MouseDownCallback(MouseDownEvent e)
        {
            // When left clicking on the graph (not a node or something else)
            if (e.button == 0)
            {
                // Close all settings windows:
                nodeViews.ForEach(v => v.CloseSettings());
            }

            if (DoesSelectionContainsInspectorNodes())
            {
                UpdateNodeInspectorSelection();
            }
        }

        private bool DoesSelectionContainsInspectorNodes()
        {
            var selectedNodes = selection.Where(s => s is BaseNodeView).ToList();
            var selectedNodesNotInInspector = selectedNodes.Except(nodeInspector.selectedNodes).ToList();
            var nodeInInspectorWithoutSelectedNodes = nodeInspector.selectedNodes.Except(selectedNodes).ToList();

            return selectedNodesNotInInspector.Any() || nodeInInspectorWithoutSelectedNodes.Any();
        }

        private void DragPerformedCallback(DragPerformEvent e)
        {
            var mousePos = (e.currentTarget as VisualElement).ChangeCoordinatesTo(contentViewContainer, e.localMousePosition);

            // External objects drag and drop
            if (DragAndDrop.objectReferences.Length > 0)
            {
                RegisterCompleteObjectUndo("Create Node From Object(s)");
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    var objectType = obj.GetType();

                    foreach (var kp in nodeTypePerCreateAssetType)
                    {
                        if (kp.Key.IsAssignableFrom(objectType))
                        {
                            try
                            {
                                var node = BaseNode.CreateFromType(kp.Value.nodeType, mousePos);
                                if ((bool)kp.Value.initalizeNodeFromObject.Invoke(node, new[] { obj }))
                                {
                                    AddNode(node);
                                    break;
                                }
                            }
                            catch (Exception exception)
                            {
                                Debug.LogException(exception);
                            }
                        }
                    }
                }
            }
        }

        private void DragUpdatedCallback(DragUpdatedEvent e)
        {
            var dragData = DragAndDrop.GetGenericData("DragSelection") as List<ISelectable>;
            var dragObjects = DragAndDrop.objectReferences;
            var dragging = false;

            if (dragData != null)
            {
                // Handle drag from exposed parameter view
                if (dragData.OfType<ExposedParameterFieldView>().Any())
                {
                    dragging = true;
                }
            }

            if (dragObjects.Length > 0)
            {
                dragging = true;
            }

            if (dragging)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
            }

            UpdateNodeInspectorSelection();
        }
        #endregion

        #region Initialization
        private void ReloadView()
        {
            // Force the graph to reload his data (Undo have updated the serialized properties of the graph
            // so the one that are not serialized need to be synchronized)
            graph.Deserialize();

            // Get selected nodes
            var selectedNodeGUIDs = new List<string>();
            foreach (var e in selection)
            {
                if (e is BaseNodeView v && Contains(v))
                {
                    selectedNodeGUIDs.Add(v.nodeTarget.GUID);
                }
            }

            // Remove everything
            RemoveNodeViews();
            RemoveEdges();
            RemoveGroups();
#if UNITY_2020_1_OR_NEWER
            RemoveStrickyNotes();
#endif
            RemoveStackNodeViews();

            UpdateSerializedProperties();

            // And re-add with new up to date datas
            InitializeNodeViews();
            InitializeEdgeViews();
            InitializeGroups();
            InitializeStickyNotes();
            InitializeStackNodes();

            Reload();

            UpdateComputeOrder();

            // Restore selection after re-creating all views
            // selection = nodeViews.Where(v => selectedNodeGUIDs.Contains(v.nodeTarget.GUID)).Select(v => v as ISelectable).ToList();
            foreach (var guid in selectedNodeGUIDs)
            {
                AddToSelection(nodeViews.FirstOrDefault(n => n.nodeTarget.GUID == guid));
            }

            UpdateNodeInspectorSelection();
        }

        public void Initialize(BaseGraph graph)
        {
            if (this.graph != null)
            {
                SaveGraphToDisk();
                // Close pinned windows from old graph:
                ClearGraphElements();
                NodeProvider.UnloadGraph(graph);
            }

            this.graph = graph;

            exposedParameterFactory = new ExposedParameterFieldFactory(graph);

            UpdateSerializedProperties();

            connectorListener = CreateEdgeConnectorListener();

            // When pressing ctrl-s, we save the graph
            EditorSceneManager.sceneSaved += _ => SaveGraphToDisk();
            RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.S && e.actionKey)
                {
                    SaveGraphToDisk();
                }
            });

            ClearGraphElements();

            InitializeGraphView();
            InitializeNodeViews();
            InitializeEdgeViews();
            InitializeViews();
            InitializeGroups();
            InitializeStickyNotes();
            InitializeStackNodes();

            initialized?.Invoke();
            UpdateComputeOrder();

            InitializeView();

            NodeProvider.LoadGraph(graph);

            // Register the nodes that can be created from assets
            foreach (var nodeInfo in NodeProvider.GetNodeMenuEntries(graph))
            {
                var interfaces = nodeInfo.type.GetInterfaces();
                var exceptInheritedInterfaces = interfaces.Except(interfaces.SelectMany(t => t.GetInterfaces()));
                foreach (var i in interfaces)
                {
                    if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICreateNodeFrom<>))
                    {
                        var genericArgumentType = i.GetGenericArguments()[0];
                        var initializeFunction = nodeInfo.type.GetMethod(
                            nameof(ICreateNodeFrom<Object>.InitializeNodeFromObject),
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                            null,
                            new[] { genericArgumentType },
                            null
                        );

                        // We only add the type that implements the interface, not it's children
                        if (initializeFunction.DeclaringType == nodeInfo.type)
                        {
                            nodeTypePerCreateAssetType[genericArgumentType] = (nodeInfo.type, initializeFunction);
                        }
                    }
                }
            }
        }

        public void ClearGraphElements()
        {
            RemoveGroups();
            RemoveNodeViews();
            RemoveEdges();
            RemoveStackNodeViews();
            RemovePinnedElementViews();
#if UNITY_2020_1_OR_NEWER
            RemoveStrickyNotes();
#endif
        }

        private void UpdateSerializedProperties()
        {
            serializedGraph = new SerializedObject(graph);
        }

        /// <summary>
        ///     Allow you to create your own edge connector listener
        /// </summary>
        /// <returns></returns>
        protected virtual BaseEdgeConnectorListener CreateEdgeConnectorListener()
        {
            return new BaseEdgeConnectorListener(this);
        }

        private void InitializeGraphView()
        {
            graph.onExposedParameterListChanged += OnExposedParameterListChanged;
            graph.onExposedParameterModified += s => onExposedParameterModified?.Invoke(s);
            graph.onGraphChanges += GraphChangesCallback;
            viewTransform.position = graph.position;
            viewTransform.scale = graph.scale;
            nodeCreationRequest = c => SearchWindow.Open(new SearchWindowContext(c.screenMousePosition), createNodeMenu);
        }

        private void OnExposedParameterListChanged()
        {
            UpdateSerializedProperties();
            onExposedParameterListChanged?.Invoke();
        }

        private void InitializeNodeViews()
        {
            graph.nodes.RemoveAll(n => n == null);

            foreach (var node in graph.nodes)
            {
                var v = AddNodeView(node);
            }
        }

        private void InitializeEdgeViews()
        {
            // Sanitize edges in case a node broke something while loading
            graph.edges.RemoveAll(edge => edge == null || edge._inputNode == null || edge._outputNode == null);

            foreach (var serializedEdge in graph.edges)
            {
                nodeViewsPerNode.TryGetValue(serializedEdge._inputNode, out var inputNodeView);
                nodeViewsPerNode.TryGetValue(serializedEdge._outputNode, out var outputNodeView);
                if (inputNodeView == null || outputNodeView == null)
                {
                    continue;
                }

                var edgeView = CreateEdgeView();
                edgeView.userData = serializedEdge;
                edgeView.input = inputNodeView.GetPortViewFromFieldName(serializedEdge.inputFieldName, serializedEdge.inputPortIdentifier);
                edgeView.output = outputNodeView.GetPortViewFromFieldName(serializedEdge.outputFieldName, serializedEdge.outputPortIdentifier);

                ConnectView(edgeView);
            }
        }

        private void InitializeViews()
        {
            foreach (var pinnedElement in graph.pinnedElements)
            {
                if (pinnedElement.opened)
                {
                    OpenPinned(pinnedElement.editorType.type, pinnedElement.GetType());
                }
            }
        }

        private void InitializeGroups()
        {
            foreach (var group in graph.groups)
            {
                AddGroupView(group);
            }
        }

        private void InitializeStickyNotes()
        {
#if UNITY_2020_1_OR_NEWER
            foreach (var group in graph.stickyNotes)
            {
                AddStickyNoteView(group);
            }
#endif
        }

        private void InitializeStackNodes()
        {
            foreach (var stackNode in graph.stackNodes)
            {
                AddStackNodeView(stackNode);
            }
        }

        protected virtual void InitializeManipulators()
        {
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
        }

        protected virtual void Reload() { }
        #endregion

        #region Graph content modification
        public void UpdateNodeInspectorSelection()
        {
            if (nodeInspector.previouslySelectedObject != Selection.activeObject)
            {
                nodeInspector.previouslySelectedObject = Selection.activeObject;
            }

            var selectedNodeViews = new HashSet<BaseNodeView>();
            nodeInspector.selectedNodes.Clear();
            foreach (var e in selection)
            {
                if (e is BaseNodeView v && Contains(v) && v.nodeTarget.needsInspector)
                {
                    selectedNodeViews.Add(v);
                }
            }

            nodeInspector.UpdateSelectedNodes(selectedNodeViews);
            if (Selection.activeObject != nodeInspector && selectedNodeViews.Count > 0)
            {
                Selection.activeObject = nodeInspector;
            }
        }

        public BaseNodeView AddNode(BaseNode node)
        {
            // This will initialize the node using the graph instance
            graph.AddNode(node);

            UpdateSerializedProperties();

            var view = AddNodeView(node);

            // Call create after the node have been initialized
            ExceptionToLog.Call(() => view.OnCreated());

            UpdateComputeOrder();

            return view;
        }

        public BaseNodeView AddNodeView(BaseNode node)
        {
            var viewType = NodeProvider.GetNodeViewTypeFromType(node.GetType());

            if (viewType == null)
            {
                viewType = typeof(BaseNodeView);
            }

            var baseNodeView = Activator.CreateInstance(viewType) as BaseNodeView;
            baseNodeView.Initialize(this, node);
            AddElement(baseNodeView);

            nodeViews.Add(baseNodeView);
            nodeViewsPerNode[node] = baseNodeView;

            return baseNodeView;
        }

        public void RemoveNode(BaseNode node)
        {
            var view = nodeViewsPerNode[node];
            RemoveNodeView(view);
            graph.RemoveNode(node);
        }

        public void RemoveNodeView(BaseNodeView nodeView)
        {
            RemoveElement(nodeView);
            nodeViews.Remove(nodeView);
            nodeViewsPerNode.Remove(nodeView.nodeTarget);
        }

        public void RemoveNodeViews()
        {
            foreach (var nodeView in nodeViews)
            {
                RemoveElement(nodeView);
            }
            nodeViews.Clear();
            nodeViewsPerNode.Clear();
        }

        private void RemoveStackNodeViews()
        {
            foreach (var stackView in stackNodeViews)
            {
                RemoveElement(stackView);
            }
            stackNodeViews.Clear();
        }

        private void RemovePinnedElementViews()
        {
            foreach (var pinnedView in pinnedElements.Values)
            {
                if (Contains(pinnedView))
                {
                    Remove(pinnedView);
                }
            }
            pinnedElements.Clear();
        }

        public GroupView AddGroup(Group block)
        {
            graph.AddGroup(block);
            block.OnCreated();
            return AddGroupView(block);
        }

        public GroupView AddGroupView(Group block)
        {
            var c = new GroupView();

            c.Initialize(this, block);

            AddElement(c);

            groupViews.Add(c);
            return c;
        }

        public BaseStackNodeView AddStackNode(BaseStackNode stackNode)
        {
            graph.AddStackNode(stackNode);
            return AddStackNodeView(stackNode);
        }

        public BaseStackNodeView AddStackNodeView(BaseStackNode stackNode)
        {
            var viewType = StackNodeViewProvider.GetStackNodeCustomViewType(stackNode.GetType()) ?? typeof(BaseStackNodeView);
            var stackView = Activator.CreateInstance(viewType, stackNode) as BaseStackNodeView;

            AddElement(stackView);
            stackNodeViews.Add(stackView);

            stackView.Initialize(this);

            return stackView;
        }

        public void RemoveStackNodeView(BaseStackNodeView stackNodeView)
        {
            stackNodeViews.Remove(stackNodeView);
            RemoveElement(stackNodeView);
        }

#if UNITY_2020_1_OR_NEWER
        public StickyNoteView AddStickyNote(StickyNote note)
        {
            graph.AddStickyNote(note);
            return AddStickyNoteView(note);
        }

        public StickyNoteView AddStickyNoteView(StickyNote note)
        {
            var c = new StickyNoteView();

            c.Initialize(this, note);

            AddElement(c);

            stickyNoteViews.Add(c);
            return c;
        }

        public void RemoveStickyNoteView(StickyNoteView view)
        {
            stickyNoteViews.Remove(view);
            RemoveElement(view);
        }

        public void RemoveStrickyNotes()
        {
            foreach (var stickyNodeView in stickyNoteViews)
            {
                RemoveElement(stickyNodeView);
            }
            stickyNoteViews.Clear();
        }
#endif

        public void AddSelectionsToGroup(GroupView view)
        {
            foreach (var selectedNode in selection)
            {
                if (selectedNode is BaseNodeView)
                {
                    if (groupViews.Exists(x => x.ContainsElement(selectedNode as BaseNodeView)))
                    {
                        continue;
                    }

                    view.AddElement(selectedNode as BaseNodeView);
                }
            }
        }

        public void RemoveGroups()
        {
            foreach (var groupView in groupViews)
            {
                RemoveElement(groupView);
            }
            groupViews.Clear();
        }

        public bool CanConnectEdge(EdgeView e, bool autoDisconnectInputs = true)
        {
            if (e.input == null || e.output == null)
            {
                return false;
            }

            var inputPortView = e.input as PortView;
            var outputPortView = e.output as PortView;
            var inputNodeView = inputPortView.node as BaseNodeView;
            var outputNodeView = outputPortView.node as BaseNodeView;

            if (inputNodeView == null || outputNodeView == null)
            {
                Debug.LogError("Connect aborted !");
                return false;
            }

            return true;
        }

        public bool ConnectView(EdgeView e, bool autoDisconnectInputs = true)
        {
            if (!CanConnectEdge(e, autoDisconnectInputs))
            {
                return false;
            }

            var inputPortView = e.input as PortView;
            var outputPortView = e.output as PortView;
            var inputNodeView = inputPortView.node as BaseNodeView;
            var outputNodeView = outputPortView.node as BaseNodeView;

            //If the input port does not support multi-connection, we remove them
            if (autoDisconnectInputs && !(e.input as PortView).portData.acceptMultipleEdges)
            {
                foreach (var edge in edgeViews.Where(ev => ev.input == e.input).ToList())
                {
                    // TODO: do not disconnect them if the connected port is the same than the old connected
                    DisconnectView(edge);
                }
            }
            // same for the output port:
            if (autoDisconnectInputs && !(e.output as PortView).portData.acceptMultipleEdges)
            {
                foreach (var edge in edgeViews.Where(ev => ev.output == e.output).ToList())
                {
                    // TODO: do not disconnect them if the connected port is the same than the old connected
                    DisconnectView(edge);
                }
            }

            AddElement(e);

            e.input.Connect(e);
            e.output.Connect(e);

            // If the input port have been removed by the custom port behavior
            // we try to find if it's still here
            if (e.input == null)
            {
                e.input = inputNodeView.GetPortViewFromFieldName(inputPortView.fieldName, inputPortView.portData.identifier);
            }
            if (e.output == null)
            {
                e.output = inputNodeView.GetPortViewFromFieldName(outputPortView.fieldName, outputPortView.portData.identifier);
            }

            edgeViews.Add(e);

            inputNodeView.RefreshPorts();
            outputNodeView.RefreshPorts();

            // In certain cases the edge color is wrong so we patch it
            schedule.Execute(() => { e.UpdateEdgeControl(); }).ExecuteLater(1);

            e.isConnected = true;

            return true;
        }

        public bool Connect(PortView inputPortView, PortView outputPortView, bool autoDisconnectInputs = true)
        {
            var inputPort = inputPortView.owner.nodeTarget.GetPort(inputPortView.fieldName, inputPortView.portData.identifier);
            var outputPort = outputPortView.owner.nodeTarget.GetPort(outputPortView.fieldName, outputPortView.portData.identifier);

            // Checks that the node we are connecting still exists
            if (inputPortView.owner.parent == null || outputPortView.owner.parent == null)
            {
                return false;
            }

            var newEdge = SerializableEdge.CreateNewEdge(graph, inputPort, outputPort);

            var edgeView = CreateEdgeView();
            edgeView.userData = newEdge;
            edgeView.input = inputPortView;
            edgeView.output = outputPortView;

            return Connect(edgeView);
        }

        public bool Connect(EdgeView e, bool autoDisconnectInputs = true)
        {
            if (!CanConnectEdge(e, autoDisconnectInputs))
            {
                return false;
            }

            var inputPortView = e.input as PortView;
            var outputPortView = e.output as PortView;
            var inputNodeView = inputPortView.node as BaseNodeView;
            var outputNodeView = outputPortView.node as BaseNodeView;
            var inputPort = inputNodeView.nodeTarget.GetPort(inputPortView.fieldName, inputPortView.portData.identifier);
            var outputPort = outputNodeView.nodeTarget.GetPort(outputPortView.fieldName, outputPortView.portData.identifier);

            e.userData = graph.Connect(inputPort, outputPort, autoDisconnectInputs);

            ConnectView(e, autoDisconnectInputs);

            UpdateComputeOrder();

            return true;
        }

        public void DisconnectView(EdgeView e, bool refreshPorts = true)
        {
            if (e == null)
            {
                return;
            }

            RemoveElement(e);

            if (e?.input?.node is BaseNodeView inputNodeView)
            {
                e.input.Disconnect(e);
                if (refreshPorts)
                {
                    inputNodeView.RefreshPorts();
                }
            }
            if (e?.output?.node is BaseNodeView outputNodeView)
            {
                e.output.Disconnect(e);
                if (refreshPorts)
                {
                    outputNodeView.RefreshPorts();
                }
            }

            edgeViews.Remove(e);
        }

        public void Disconnect(EdgeView e, bool refreshPorts = true)
        {
            // Remove the serialized edge if there is one
            if (e.userData is SerializableEdge serializableEdge)
            {
                graph.Disconnect(serializableEdge.GUID);
            }

            DisconnectView(e, refreshPorts);

            UpdateComputeOrder();
        }

        public void RemoveEdges()
        {
            foreach (var edge in edgeViews)
            {
                RemoveElement(edge);
            }
            edgeViews.Clear();
        }

        public void UpdateComputeOrder()
        {
            graph.UpdateComputeOrder();

            computeOrderUpdated?.Invoke();
        }

        public void RegisterCompleteObjectUndo(string name)
        {
            Undo.RegisterCompleteObjectUndo(graph, name);
        }

        public void SaveGraphToDisk()
        {
            if (graph == null)
            {
                return;
            }

            // Get the asset path
            var assetPath = AssetDatabase.GetAssetPath(graph);
            if (string.IsNullOrEmpty(assetPath))
            {
                // If no asset path, this might be a new asset, so just save normally
                EditorUtility.SetDirty(graph);
                AssetDatabase.SaveAssets();
                return;
            }

            // Create a temporary copy of the current asset
            var tempPath = assetPath + ".temp";

            try
            {
                // Copy the current asset to temp
                AssetDatabase.CopyAsset(assetPath, tempPath);

                // Load the temp asset
                var tempGraph = AssetDatabase.LoadAssetAtPath<BaseGraph>(tempPath);
                if (tempGraph == null)
                {
                    // Fallback to normal save if temp creation fails
                    EditorUtility.SetDirty(graph);
                    AssetDatabase.SaveAssets();
                    return;
                }

                // Apply current changes to the temp asset
                EditorUtility.CopySerialized(graph, tempGraph);
                EditorUtility.SetDirty(tempGraph);

                // Force Unity to serialize the temp asset
                AssetDatabase.SaveAssets();

                // Compare the actual file contents
                var originalBytes = File.ReadAllBytes(assetPath);
                var tempBytes = File.ReadAllBytes(tempPath);

                // Compare the bytes
                var hasChanges = !originalBytes.SequenceEqual(tempBytes);

                if (hasChanges)
                {
                    // There are actual changes, so save the real asset
                    EditorUtility.SetDirty(graph);
                    AssetDatabase.SaveAssets();
                }
                // If no changes, we don't save anything
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error during byte comparison save: {e.Message}. Falling back to normal save.");
                // Fallback to normal save if anything goes wrong
                EditorUtility.SetDirty(graph);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                // Clean up the temp file
                if (AssetDatabase.AssetPathExists(tempPath))
                {
                    AssetDatabase.DeleteAsset(tempPath);
                }
            }
        }

        public void ToggleView<TView, TModel>()
            where TView : PinnedElementView
            where TModel : PinnedElement
        {
            ToggleView(typeof(TView), typeof(TModel));
        }

        public void ToggleView(Type viewType, Type modelType)
        {
            PinnedElementView view;
            pinnedElements.TryGetValue(viewType, out view);

            if (view == null)
            {
                OpenPinned(viewType, modelType);
            }
            else
            {
                ClosePinned(viewType, view);
            }
        }

        public void OpenPinned<TView, TModel>()
            where TView : PinnedElementView
            where TModel : PinnedElement
        {
            OpenPinned(typeof(TView), typeof(TModel));
        }

        public void OpenPinned(Type viewType, Type modelType)
        {
            PinnedElementView view;

            if (viewType == null)
            {
                return;
            }

            var elem = graph.OpenPinned(viewType, modelType);

            if (!pinnedElements.ContainsKey(viewType))
            {
                view = Activator.CreateInstance(viewType) as PinnedElementView;
                if (view == null)
                {
                    return;
                }
                pinnedElements[viewType] = view;
                view.InitializeGraphView(elem, this);
            }
            view = pinnedElements[viewType];

            if (!Contains(view))
            {
                Add(view);
            }
        }

        public void ClosePinned<T>(PinnedElementView view) where T : PinnedElementView
        {
            ClosePinned(typeof(T), view);
        }

        public void ClosePinned(Type type, PinnedElementView elem)
        {
            pinnedElements.Remove(type);
            Remove(elem);
            graph.ClosePinned(type);
        }

        public Status GetPinnedElementStatus<T>() where T : PinnedElementView
        {
            return GetPinnedElementStatus(typeof(T));
        }

        public Status GetPinnedElementStatus(Type type)
        {
            var pinned = graph.pinnedElements.Find(p => p.editorType.type == type);

            if (pinned != null && pinned.opened)
            {
                return Status.Normal;
            }
            return Status.Hidden;
        }

        public void ResetPositionAndZoom()
        {
            graph.position = Vector3.zero;
            graph.scale = Vector3.one;

            UpdateViewTransform(graph.position, graph.scale);
        }

        /// <summary>
        ///     Deletes the selected content, can be called form an IMGUI container
        /// </summary>
        public void DelayedDeleteSelection()
        {
            schedule.Execute(() => DeleteSelectionOperation("Delete", AskUser.DontAskUser)).ExecuteLater(0);
        }

        protected virtual void InitializeView() { }

        public virtual IEnumerable<(string path, Type type)> FilterCreateNodeMenuEntries()
        {
            // By default we don't filter anything
            foreach (var nodeMenuItem in NodeProvider.GetNodeMenuEntries(graph))
            {
                yield return nodeMenuItem;
            }

            // TODO: add exposed properties to this list
        }

        public RelayNodeView AddRelayNode(PortView inputPort, PortView outputPort, Vector2 position)
        {
            var relayNode = BaseNode.CreateFromType<RelayNode>(position);
            var view = AddNode(relayNode) as RelayNodeView;

            if (outputPort != null)
            {
                Connect(view.inputPortViews[0], outputPort);
            }
            if (inputPort != null)
            {
                Connect(inputPort, view.outputPortViews[0]);
            }

            return view;
        }

        /// <summary>
        ///     Update all the serialized property bindings (in case a node was deleted / added, the property pathes needs to be
        ///     updated)
        /// </summary>
        public void SyncSerializedPropertyPathes()
        {
            foreach (var nodeView in nodeViews)
            {
                nodeView.SyncSerializedPropertyPaths();
            }
            nodeInspector.RefreshNodes();
        }

        /// <summary>
        ///     Call this function when you want to remove this view
        /// </summary>
        public void Dispose()
        {
            ClearGraphElements();
            RemoveFromHierarchy();
            Undo.undoRedoPerformed -= ReloadView;
            Object.DestroyImmediate(nodeInspector);
            NodeProvider.UnloadGraph(graph);
            if (exposedParameterFactory != null)
            {
                exposedParameterFactory.Dispose();
                exposedParameterFactory = null;
            }
            graph.onExposedParameterListChanged -= OnExposedParameterListChanged;
            graph.onExposedParameterModified += s => onExposedParameterModified?.Invoke(s);
            graph.onGraphChanges -= GraphChangesCallback;
        }
        #endregion
    }
}