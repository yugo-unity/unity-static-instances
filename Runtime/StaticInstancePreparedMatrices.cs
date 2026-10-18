using System;
using System.Collections.Generic;
using UnityEngine;

namespace StaticInstances
{
    /// <summary>Serialized matrix storage. No decoded matrix array is cached.</summary>
    [Serializable]
    internal sealed class StaticInstancePreparedMatrices
    {
        [SerializeField] internal int Format;
        [SerializeField] internal int Count;
        [SerializeField] internal string Producer;
        [SerializeField] internal bool LittleEndian;
        [SerializeField] internal float[] Values;
        [SerializeField] internal int[] BottomRowIndices;
        [SerializeField] internal Vector4[] BottomRows;
        [SerializeField] internal Hash128 Digest;

        internal bool IsPresent => Format != 0 || Count != 0 || (Values?.Length ?? 0) != 0 ||
            (BottomRowIndices?.Length ?? 0) != 0 || (BottomRows?.Length ?? 0) != 0 ||
            !string.IsNullOrEmpty(Producer) || Digest != default;
        internal bool HasValidShape => Format == 1 && Count > 0 &&
            Count <= int.MaxValue / 24 && Values != null && Values.Length == 24 * Count &&
            (BottomRowIndices?.Length ?? 0) == (BottomRows?.Length ?? 0);
        internal bool MatchesProducer => Producer == Application.unityVersion &&
            LittleEndian == BitConverter.IsLittleEndian;

        internal static StaticInstancePreparedMatrices Create(Matrix4x4[] matrices)
        {
            if (matrices == null || matrices.Length == 0)
                return null;
            var result = new StaticInstancePreparedMatrices
            {
                Format = 1,
                Count = matrices.Length,
                Producer = Application.unityVersion,
                LittleEndian = BitConverter.IsLittleEndian,
                Values = new float[checked(matrices.Length * 24)],
            };
            List<int> indices = null;
            List<Vector4> rows = null;
            for (int i = 0; i < matrices.Length; i++)
            {
                Matrix4x4 m = matrices[i];
                Write(result.Values, 12 * i, m);
                Write(result.Values, 12 * (matrices.Length + i), m.inverse);
                if (BitConverter.SingleToInt32Bits(m.m30) == 0 &&
                    BitConverter.SingleToInt32Bits(m.m31) == 0 &&
                    BitConverter.SingleToInt32Bits(m.m32) == 0 &&
                    BitConverter.SingleToInt32Bits(m.m33) == 0x3f800000)
                    continue;
                if (indices == null)
                {
                    indices = new List<int>();
                    rows = new List<Vector4>();
                }
                indices.Add(i);
                rows.Add(new Vector4(m.m30, m.m31, m.m32, m.m33));
            }
            result.BottomRowIndices = indices?.ToArray() ?? Array.Empty<int>();
            result.BottomRows = rows?.ToArray() ?? Array.Empty<Vector4>();
            result.Digest = result.ComputeDigest();
            return result;
        }

        internal bool Validate()
        {
            if (!HasValidShape || string.IsNullOrEmpty(Producer))
                return false;
            int previous = -1;
            for (int i = 0; i < (BottomRowIndices?.Length ?? 0); i++)
            {
                int index = BottomRowIndices[i];
                if (index <= previous || index >= Count)
                    return false;
                previous = index;
            }
            return Digest == ComputeDigest();
        }

        // Call only after Validate. Used by explicit queries and producer fallback.
        internal Matrix4x4[] Decode()
        {
            var result = new Matrix4x4[Count];
            for (int i = 0; i < Count; i++)
            {
                Matrix4x4 m = Matrix4x4.identity;
                int offset = 12 * i;
                for (int column = 0; column < 4; column++)
                    for (int row = 0; row < 3; row++)
                        m[row, column] = Values[offset++];
                result[i] = m;
            }
            for (int i = 0; i < (BottomRowIndices?.Length ?? 0); i++)
                result[BottomRowIndices[i]].SetRow(3, BottomRows[i]);
            return result;
        }

        private Hash128 ComputeDigest()
        {
            var hash = new Hash128();
            hash.Append(Format);
            hash.Append(Count);
            hash.Append(Producer);
            hash.Append(LittleEndian ? 1 : 0);
            hash.Append(Values);
            if ((BottomRowIndices?.Length ?? 0) > 0)
            {
                hash.Append(BottomRowIndices);
                hash.Append(BottomRows);
            }
            return hash;
        }

        private static void Write(float[] values, int offset, Matrix4x4 matrix)
        {
            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 3; row++)
                    values[offset++] = matrix[row, column];
        }
    }
}
