using System.Runtime.InteropServices;

namespace FolderToFileName
{
    public static class NaturalStringComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);

        public static int Compare(string? x, string? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return StrCmpLogicalW(x, y);
        }

        /// <summary>
        /// 先比较各级目录路径，同一目录下再比较文件名（均使用 Windows 自然排序算法）
        /// </summary>
        public static int ComparePath(string relativePath1, string relativePath2)
        {
            string[] parts1 = relativePath1.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            string[] parts2 = relativePath2.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

            int dirCount1 = parts1.Length - 1;
            int dirCount2 = parts2.Length - 1;

            // 1. 先逐级比较上层目录
            int minDirs = Math.Min(dirCount1, dirCount2);
            for (int i = 0; i < minDirs; i++)
            {
                int cmp = Compare(parts1[i], parts2[i]);
                if (cmp != 0) return cmp;
            }

            // 2. 如果目录层级深度不同（如根目录文件 vs 子目录文件），目录层级浅的排在前面
            if (dirCount1 != dirCount2)
            {
                return dirCount1.CompareTo(dirCount2);
            }

            // 3. 目录相同，最后比较文件名
            return Compare(parts1[^1], parts2[^1]);
        }

        public class NaturalComparer : IComparer<string>
        {
            public int Compare(string? x, string? y) => NaturalStringComparer.Compare(x, y);
        }
    }
}