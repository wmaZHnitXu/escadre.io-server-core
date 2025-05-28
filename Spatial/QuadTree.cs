// File: Core/Spatial/QuadTree.cs
using Core.Primitives;
using System.Collections.Generic;
using Core.Logging; // Assuming Core.Logging.Logger is available

namespace Core.Spatial
{
    // Internal class to hold item data within the QuadTree
    internal class QuadTreeItem<T>
    {
        public T Item { get; }
        public RectFloat Bounds { get; set; } // Store precise bounds for updates

        public QuadTreeItem(T item, RectFloat bounds)
        {
            Item = item;
            Bounds = bounds;
        }
    }

    public class QuadTreeNode<T>
    {
        public RectFloat Bounds { get; }
        public int Depth { get; }
        private readonly int _maxItems;
        private readonly int _maxDepth;

        private List<QuadTreeItem<T>> _items;
        private QuadTreeNode<T>[] _children; // Top-left, Top-right, Bottom-left, Bottom-right

        public bool IsLeaf => _children == null;

        public QuadTreeNode(RectFloat bounds, int depth, int maxItems, int maxDepth)
        {
            Bounds = bounds;
            Depth = depth;
            _maxItems = maxItems;
            _maxDepth = maxDepth;
            _items = new List<QuadTreeItem<T>>();
        }

        public void Clear()
        {
            _items.Clear();
            if (!IsLeaf)
            {
                foreach (var child in _children)
                {
                    child.Clear();
                }
                _children = null; // Become a leaf again
            }
        }

        internal bool Insert(QuadTreeItem<T> item)
        {
            // If item doesn't belong in this node or its children, return false
            if (!Bounds.Intersects(item.Bounds)) // Use Intersects for items that might be larger than cells
            {
                return false;
            }

            if (IsLeaf)
            {
                _items.Add(item);

                // If capacity is exceeded and we can split further
                if (_items.Count > _maxItems && Depth < _maxDepth)
                {
                    Split();
                }
                return true;
            }
            else // Not a leaf, try inserting into children
            {
                bool inserted = false;
                foreach (var child in _children)
                {
                    if (child.Insert(item))
                    {
                        inserted = true;
                        // An item might be inserted into multiple children if it spans across them.
                        // Or, if an item should only belong to one child, pick one (e.g., child.Bounds.Contains(item.Bounds.Center))
                        // For simplicity and correctness with items larger than cells, allow insertion if it intersects.
                        // However, this might lead to duplicates if not handled carefully during query.
                        // A common approach is to only store items in leaves, or if an item cannot fit wholly in a child, store it in parent.
                        // Let's use: if item fits wholly in a child, pass it down. Otherwise, store in parent.
                        // This changes the Insert logic a bit.

                        // Simpler: if an item intersects a child, it *could* belong there.
                        // If an item is small enough to fit in a child, it must go down.
                        // If an item spans multiple children, it must stay in this parent node.
                        // The current logic (add to _items list if leaf, else pass to children) implies items are only stored in leaves *after* splitting.
                        // Let's stick to: store items in nodes if they cannot be pushed further down.
                        // This requires adjusting insert. Let's simplify for now: items are stored in the node they are inserted into if leaf, or passed to children.
                        // This means items are effectively stored in leaves.
                    }
                }
                 // If the item was passed down to children, it's considered "inserted" at this level's perspective for the caller.
                return true; // Or return 'inserted' if we want to be strict.
            }
        }
        
        // More robust Insert: an item belongs to the smallest node that fully contains it.
        // If it spans children, it stays in the parent.
        internal bool RobustInsert(QuadTreeItem<T> item)
        {
            if (!Bounds.Intersects(item.Bounds))
            {
                return false;
            }

            if (!IsLeaf)
            {
                int childIndex = GetChildIndexForItem(item.Bounds);
                if (childIndex != -1) // Fits wholly in one child
                {
                    return _children[childIndex].RobustInsert(item);
                }
            }

            // If it's a leaf, or doesn't fit wholly in any child, add to this node.
            _items.Add(item);

            if (IsLeaf && _items.Count > _maxItems && Depth < _maxDepth)
            {
                Split(); // After splitting, items in this node need to be re-distributed.
            }
            return true;
        }


        private int GetChildIndexForItem(RectFloat itemBounds)
        {
            // Determine which child the item's bounds completely fit into.
            // If it spans multiple or doesn't fit, return -1.
            int index = -1;
            float midX = Bounds.X + Bounds.Width * 0.5f;
            float midY = Bounds.Y + Bounds.Height * 0.5f;

            bool topQuadrant = itemBounds.MaxY < midY; // item is fully in top half
            bool bottomQuadrant = itemBounds.MinY >= midY; // item is fully in bottom half
            bool leftQuadrant = itemBounds.MaxX < midX; // item is fully in left half
            bool rightQuadrant = itemBounds.MinX >= midX; // item is fully in right half
            
            if (topQuadrant)
            {
                if (leftQuadrant) index = 0; // Top-Left
                else if (rightQuadrant) index = 1; // Top-Right
            }
            else if (bottomQuadrant)
            {
                if (leftQuadrant) index = 2; // Bottom-Left
                else if (rightQuadrant) index = 3; // Bottom-Right
            }
            return index;
        }


        private void Split()
        {
            float childWidth = Bounds.Width * 0.5f;
            float childHeight = Bounds.Height * 0.5f;
            int nextDepth = Depth + 1;

            _children = new QuadTreeNode<T>[4];
            _children[0] = new QuadTreeNode<T>(new RectFloat(Bounds.X, Bounds.Y, childWidth, childHeight), nextDepth, _maxItems, _maxDepth); // Top-Left
            _children[1] = new QuadTreeNode<T>(new RectFloat(Bounds.X + childWidth, Bounds.Y, childWidth, childHeight), nextDepth, _maxItems, _maxDepth); // Top-Right
            _children[2] = new QuadTreeNode<T>(new RectFloat(Bounds.X, Bounds.Y + childHeight, childWidth, childHeight), nextDepth, _maxItems, _maxDepth); // Bottom-Left
            _children[3] = new QuadTreeNode<T>(new RectFloat(Bounds.X + childWidth, Bounds.Y + childHeight, childWidth, childHeight), nextDepth, _maxItems, _maxDepth); // Bottom-Right

            // Re-distribute items from this node to children
            List<QuadTreeItem<T>> itemsToReInsert = new List<QuadTreeItem<T>>(_items);
            _items.Clear(); // Items will now live in children or stay if they span

            foreach (var item in itemsToReInsert)
            {
                // Using the original simpler insert that passes to children if not leaf.
                // Or, more correctly with robust insert idea:
                bool insertedIntoChild = false;
                if (!IsLeaf) // Should be true now
                {
                     int childIndex = GetChildIndexForItem(item.Bounds);
                     if (childIndex != -1)
                     {
                         _children[childIndex].RobustInsert(item);
                         insertedIntoChild = true;
                     }
                }
                if (!insertedIntoChild) // Stays in this node (now non-leaf but holds spanning items)
                {
                    _items.Add(item);
                }
            }
        }
        
        public bool Remove(T itemToRemove, RectFloat itemBounds) // Need bounds for efficient search
        {
            if (!Bounds.Intersects(itemBounds))
            {
                return false;
            }

            bool removed = false;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                // Equality check for T might be tricky if T is not just int.
                // Assuming T has a good Equals implementation (e.g. if T is int entityId).
                if (_items[i].Item.Equals(itemToRemove) && _items[i].Bounds.Equals(itemBounds))
                {
                    _items.RemoveAt(i);
                    removed = true;
                    break; // Assuming unique items or only remove one instance
                }
            }

            if (!IsLeaf && !removed) // If not found here and not a leaf, try children
            {
                foreach (var child in _children)
                {
                    if (child.Remove(itemToRemove, itemBounds))
                    {
                        removed = true;
                        break;
                    }
                }
            }
            
            // Optional: Implement merging of children if this node + children become too empty.
            // For simplicity, not implemented here.

            return removed;
        }

        internal void Query(RectFloat queryArea, List<QuadTreeItem<T>> foundItems)
        {
            if (!Bounds.Intersects(queryArea))
            {
                return;
            }

            // Add items from this node that intersect the query area
            foreach (var item in _items)
            {
                if (item.Bounds.Intersects(queryArea))
                {
                    foundItems.Add(item);
                }
            }

            // If not a leaf, query children
            if (!IsLeaf)
            {
                foreach (var child in _children)
                {
                    child.Query(queryArea, foundItems);
                }
            }
        }
    }

    /// <summary>
    /// A QuadTree for spatial partitioning of items with 2D bounds.
    /// T is typically an Entity ID (int).
    /// </summary>
    public class QuadTree<T>
    {
        private QuadTreeNode<T> _root;
        private Dictionary<T, QuadTreeItem<T>> _itemLookup; // For quick access to item and its current node/bounds for updates/removals

        public QuadTree(RectFloat worldBounds, int nodeCapacity = 4, int maxDepth = 8)
        {
            _root = new QuadTreeNode<T>(worldBounds, 0, nodeCapacity, maxDepth);
            _itemLookup = new Dictionary<T, QuadTreeItem<T>>();
        }

        public void Insert(T item, RectFloat itemBounds)
        {
            if (_itemLookup.ContainsKey(item))
            {
                // Logger.LogWarning($"[QuadTree] Item {item} already exists. Updating its bounds.");
                Update(item, itemBounds);
                return;
            }

            var qtItem = new QuadTreeItem<T>(item, itemBounds);
            // _root.Insert(qtItem); // Simpler insert
            _root.RobustInsert(qtItem); // Robust insert
            _itemLookup[item] = qtItem;
        }

        public bool Remove(T item)
        {
            if (_itemLookup.TryGetValue(item, out var qtItem))
            {
                _itemLookup.Remove(item);
                // To remove from nodes, we need its bounds.
                return _root.Remove(qtItem.Item, qtItem.Bounds);
            }
            return false;
        }

        public void Update(T item, RectFloat newItemBounds)
        {
            if (_itemLookup.TryGetValue(item, out var qtItem))
            {
                // Simple update: remove and re-insert. More complex updates could try to move it between nodes.
                _root.Remove(qtItem.Item, qtItem.Bounds); // Use old bounds for removal
                qtItem.Bounds = newItemBounds; // Update stored bounds
                // _root.Insert(qtItem); // Re-insert with new bounds
                _root.RobustInsert(qtItem);
            }
            else
            {
                Insert(item, newItemBounds); // If not found, insert it.
            }
        }

        public List<T> Query(RectFloat queryArea)
        {
            List<QuadTreeItem<T>> foundInternalItems = new List<QuadTreeItem<T>>();
            _root.Query(queryArea, foundInternalItems);

            // Use HashSet to ensure unique items if an item spanning nodes could be added multiple times by query
            // (though RobustInsert logic aims to prevent item duplication across tree levels)
            HashSet<T> uniqueItems = new HashSet<T>();
            foreach(var internalItem in foundInternalItems)
            {
                uniqueItems.Add(internalItem.Item);
            }
            return new List<T>(uniqueItems);
        }
        
        public List<T> QueryRadius(Vector2 center, float radius)
        {
            // Create a bounding box for the circle to use for initial QuadTree query
            RectFloat queryBounds = new RectFloat(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            List<T> candidateItems = Query(queryBounds);
            
            // Filter candidates by precise circle check
            List<T> finalItems = new List<T>();
            float radiusSq = radius * radius;
            foreach (T itemId in candidateItems)
            {
                if (_itemLookup.TryGetValue(itemId, out var qtItem))
                {
                    // Check if the item's bounds center is within the radius of the query center.
                    // This is an approximation. For perfect check, need to check intersection of circle and item's RectFloat.
                    // A simpler check: distance from query center to item's center vs. query radius + item's effective radius.
                    // For now, let's use if any part of the item's bounds is in the circle.
                    // This can be complex. A common simplification is if item's center is in radius, or if query center is in item's expanded bounds.
                    // Or check if item's Rect intersects the circle.
                    // Let's check if the center of the item's bounds is within the query radius from the query center.
                    if ((qtItem.Bounds.Center - center).SqrMagnitude <= radiusSq)
                    {
                        finalItems.Add(itemId);
                    }
                    // More accurate (but more expensive) check would be RectIntersectsCircle(qtItem.Bounds, center, radius)
                }
            }
            return finalItems;
        }


        public void Clear()
        {
            _root.Clear();
            _itemLookup.Clear();
        }
    }
}