using System.Collections.Generic;
using System.Linq;
using System;
namespace MZTools {
    internal static class ListReorder
    {
        internal static bool MoveItems<T>(IList<T> items, IEnumerable<int> sourceIndices, int insertionIndex)
        {
            int[] indices = sourceIndices
                .Where(index => index >= 0 && index < items.Count)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            if (indices.Length == 0)
            {
                return false;
            }

            insertionIndex = Math.Clamp(insertionIndex, 0, items.Count);
            List<T> movedItems = indices.Select(index => items[index]).ToList();
            List<T> reordered = items.ToList();
            for (int index = indices.Length - 1; index >= 0; index--)
            {
                reordered.RemoveAt(indices[index]);
            }

            int adjustedInsertionIndex = insertionIndex - indices.Count(index => index < insertionIndex);
            reordered.InsertRange(adjustedInsertionIndex, movedItems);
            if (items.SequenceEqual(reordered))
            {
                return false;
            }

            items.Clear();
            foreach (T item in reordered)
            {
                items.Add(item);
            }
            return true;
        }
    }

}

