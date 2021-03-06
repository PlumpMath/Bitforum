﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Block.cs" company="Sean McElroy">
//   Copyright Sean McElroy 2016.  Released under the terms of the MIT License
// </copyright>
// <summary>
//   A block is a unit of mining work that contains posts and the hashes that conform to the difficulty target
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Bitforum.Core
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;

    using JetBrains.Annotations;

    /// <summary>
    /// A block is a unit of mining work that contains posts and the hashes that conform to the difficulty target
    /// </summary>
    [PublicAPI]
    public class Block
    {
        /// <summary>
        /// An instance of a hashing algorithm
        /// </summary>
        private static readonly SHA512 Hasher = SHA512.Create();

        /// <summary>
        /// Gets or sets the header for this block
        /// </summary>
        public BlockHeader Header { get; set; } = new BlockHeader();

        /// <summary>
        /// Gets or sets the posts in the block
        /// </summary>
        public Post[] Posts { get; set; }

        /// <summary>
        /// Gets the default directory for storing blocks for the block chain on local storage
        /// </summary>
        /// <returns>The path to the directory in which blocks are stored</returns>
        [NotNull, Pure]
        public static string GetBlockDirectory(string suffix = null)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), $"Data{suffix}{Path.DirectorySeparatorChar}Blocks");
        }

        /// <summary>
        /// Gets the hash of the latest block in the index
        /// </summary>
        /// <param name="directory">The directory path to which to retrieve the block index</param>
        /// <returns>The hash of the latest block in the index</returns>
        [Pure, CanBeNull]
        public static string GetLatestBlockHash([NotNull] string directory)
        {
            var blockIndex = BlockIndex.LoadFromFile(directory);
            return blockIndex?.Last()?.Hash;
        }

        /// <summary>
        /// Computes a hash for this block for the given pre-calculated header and a given nonce value
        /// </summary>
        /// <param name="nonce">The value to which to apply to the last eight bytes of the <paramref name="preNonceArray"/> before calculating the hash</param>
        /// <param name="preNonceArray">The output from <see cref="BlockHeader.ToByteArray"/> for the header of this block</param>
        /// <returns>The hash for given pre-calculated header and a given nonce value</returns>
        [NotNull, Pure]
        public static byte[] HashForNonce(long nonce, [NotNull] byte[] preNonceArray)
        {
            Array.Copy(BitConverter.GetBytes(nonce), 0, preNonceArray, preNonceArray.Length - 8, 8);
            return Hasher.ComputeHash(preNonceArray);
        }

        [NotNull, Pure]
        public static Block MineBlock([NotNull] Block tail, [NotNull] List<Post> unconfirmedPosts)
        {
            if (tail.Header == null)
            {
                throw new InvalidOperationException("Tail block header is null!");
            }

            var newBlock = new Block
            {
                Header = new BlockHeader
                             {
                                Version = 1,
                                PreviousBlockHeaderHash = tail.Header.GetHash()
                },
                Posts = unconfirmedPosts.ToArray()
            };

            var minedHashNonce = newBlock.HashForZeroCount(3);
            Debug.Assert(newBlock.Header != null, "newBlock.Header != null");
            newBlock.Header.Nonce = minedHashNonce;

            return newBlock;
        }

        /// <summary>
        /// Mines a <see cref="BlockHeader.Nonce"/> value that can cause this block to serve as the genesis block
        /// </summary>
        public void GenerateGenesisHash()
        {
            if (this.Header == null)
            {
                throw new InvalidOperationException("Block header is null!");
            }

            var minedHashNonce = this.HashForZeroCount(3);
            this.Header.Nonce = minedHashNonce;
        }

        /// <summary>
        /// Saves the block to local storage
        /// </summary>
        /// <param name="directory">The directory path to which to save the block file</param>
        /// <param name="filename">The filename of the block file</param>
        public void SaveToFile([NotNull] string directory, string filename = null)
        {
            if (this.Header == null)
            {
                throw new InvalidOperationException("Block header is null!");
            }
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var fs = new FileStream(
                Path.Combine(directory, filename ?? BitConverter.ToString(this.Header.GetHash()).Replace("-", string.Empty)) + ".block",
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(this.ToByteArray());
            }
        }

        /// <summary>
        /// Returns the byte representation of this block, including the header and all posts
        /// </summary>
        /// <returns>The byte representation of this block, including the header and all posts</returns>
        [NotNull, Pure]
        public byte[] ToByteArray()
        {
            if (this.Header == null)
            {
                throw new InvalidOperationException("No header exists in the block");
            }

            if (this.Posts == null)
            {
                throw new InvalidOperationException("No posts exist in the block");
            }

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                // Write the block header
                bw.Write(this.Header.ToByteArray());

                // Write the number of fixed-width post headers
                bw.Write(this.Posts.Length);

                // Write the post bodies
                foreach (var p in this.Posts)
                {
                    bw.Write(p.Body ?? new byte[0]);
                }

                return ms.ToArray();
            }
        }

        public bool Verify()
        {
            // Verify the header.
            if (this.Header?.MerkleRootHash == null)
            {
                return false;
            }

            // Does the merkle root in the hader actually match the calculated Merkle root?
            if (BitConverter.ToString(this.GenerateMerkleRoot()) != BitConverter.ToString(this.Header.MerkleRootHash))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Generates the Merkle root hash of the unconfirmed posts that are part of this mined block
        /// </summary>
        /// <returns>The Merkle root hash of the unconfirmed posts</returns>
        [NotNull, Pure]
        [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "Reviewed. Suppression is OK here.")]
        private byte[] GenerateMerkleRoot()
        {
            var leafHashes = new List<byte[]>();
            using (var sha = SHA512.Create())
            {
                Debug.Assert(sha != null, "sha != null");
                Debug.Assert(this.Posts != null, "this.Posts");
                foreach (var t in this.Posts)
                {
                    leafHashes.Add(sha.ComputeHash(sha.ComputeHash(t.ToByteArray())));
                }
            }

            var intermediateHashes = leafHashes.ToArray();
            do
            {
                intermediateHashes = this.GenerateMerkleTree(intermediateHashes);
            }
            while (intermediateHashes.Length > 1);

            Debug.Assert(intermediateHashes != null, "intermediateHashes != null");
            Debug.Assert(intermediateHashes.Length == 1, "intermediateHashes.Length == 1");
            Debug.Assert(intermediateHashes[0] != null, "intermediateHashes[0] != null");
            return intermediateHashes[0];
        }

        [NotNull, Pure]
        private byte[][] GenerateMerkleTree([NotNull] byte[][] hashes)
        {
            var results = new List<byte[]>();

            for (var i = 0; i < hashes.Length; i += 2)
            {
                var hashA = hashes[i];
                var hashB = !(i + 1 < hashes.Length) ? hashes[i] : hashes[i + 1];
                Debug.Assert(hashB != null, "hashB != null");
                var hashCombined = new byte[hashA.Length + hashB.Length];
                Array.Copy(hashA, 0, hashCombined, 0, hashA.Length);
                Array.Copy(hashB, 0, hashCombined, hashA.Length - 1, hashB.Length);
                results.Add(Hasher.ComputeHash(Hasher.ComputeHash(hashCombined)));
            }

            return results.ToArray();
        }

        /// <summary>
        /// Retrieves the <see cref="BlockHeader.Nonce"/> value that would make this block have the <paramref name="zeroCount"/> number of zeros at the
        /// start of its <see cref="BlockHeader.GetHash"/> result
        /// </summary>
        /// <param name="zeroCount">The number of zeros for which to find the first nonce</param>
        /// <returns>The <see cref="BlockHeader.Nonce"/> value that makes this block's header have the required number of leading zeros</returns>
        [Pure]
        private uint HashForZeroCount(int zeroCount)
        {
            this.Header.MerkleRootHash = this.GenerateMerkleRoot();
            var preNonceArray = this.Header.ToByteArray();

            return HashUtility.HashForZeroCount(preNonceArray, zeroCount);
        }
    }
}
