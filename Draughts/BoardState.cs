﻿using Draughts.Pieces;
using Draughts.Rules;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Draughts.Utils;

namespace Draughts
{
    /// <summary>
    /// Stuct representing a state of 8x8 board in a game
    /// </summary>
    public struct BoardState
    {

        /* Following encoding of the board is used: 
         * Board contains only 32 places, that can hold a piece
         *  -> bit in var piecePresence tells if any piece is present
         *  -> bit in var pieceRanks tells the rank
         *  -> bit in var pieceColor tells the color
         *  
         *  
         *  Numbering (bit positions)(example for 8x8):
         *    
         *                        [Black]
         *     column:  0   1   2   3   4   5   6   7
         *           ________________________________
         *   row: 0 |   x   0   x   1   x   2   x   3
         *        1 |   4   x   5   x   6   x   7   x
         *        2 |   x   8   x   9   x  10   x  11
         *        3 |  12   x  13   x  14   x  15   x
         *        4 |   x  16   x  17   x  18   x  19
         *        5 |  20   x  21   x  22   x  23   x
         *        6 |   x  24   x  25   x  26   x  27
         *        7 |  28   x  29   x  30   x  31   x
         *                        [White]
         *                        
         *  Board state is always set this way
        */

        private ulong piecePresence, pieceRanks, pieceColors;
        public PieceColor OnMove { get; private set; }
        public readonly GameRules rules;

        public byte NumberOfColumns => rules.numberOfColumns;
        public byte NumberOfRows => rules.numberOfRows;

        public BoardState(GameRules rules, ulong piecePresence, ulong pieceRanks, ulong pieceColors, PieceColor startingColor)
        {
            this.rules = rules ?? throw new ArgumentNullException("rules can not be null");
            this.piecePresence = piecePresence;
            this.pieceRanks = pieceRanks;
            this.pieceColors = pieceColors;

            if (startingColor == PieceColor.None)
            {
                throw new ArgumentException("Invalid color");
            }
            OnMove = startingColor;
        }

        /// <summary>
        /// Shortcut
        /// </summary>
        public List<Move> GetAvaiableMoves() => rules.GetAvaiableMoves(this);
        public bool IsInsideBoard(Position pos)
        {
            return pos.column < NumberOfColumns && pos.row < NumberOfRows;
        }

        private int GetBitPosition(Position pos)
        {
            return ((pos.row * rules.numberOfColumns) + pos.column) >> 1;  // ((row * numberOfColumns) + column) / 2
        }
        public PieceType GetPieceType(Position pos)
        {
#if DEBUG
            // Invalid position
            if (!IsInsideBoard(pos))
            {
                throw new ArgumentOutOfRangeException("Invalid position on a board");
            }
#endif

            if (((pos.row + pos.column) & 0b_1) == 0b_1)
            {
                int bitPosition = GetBitPosition(pos);
                if ((piecePresence >> bitPosition & 0b_1) == 1)
                {

                    return (PieceType)(((pieceRanks >> bitPosition & 0b_1) << 1) | (pieceColors >> bitPosition & 0b_1) | 0b_100);
                }
            }

            return PieceType.None;
        }
        public BoardState SetPiece(Position pos, PieceType pieceType)
        {
            var copy = this;

#if DEBUG
            // Invalid position
            if (!IsInsideBoard(pos) || (pos.column + pos.row) % 2 != 1)
            {
                throw new ArgumentOutOfRangeException("Invalid position on a board");
            }
#endif

            int bitPosition = GetBitPosition(pos);
            copy.pieceRanks = pieceRanks & ~(0b_1U << bitPosition);  // remove piece rank
            copy.pieceColors = pieceColors & ~(0b_1U << bitPosition);  // remove piece color

            if (pieceType == PieceType.None)
            {
                copy.piecePresence = piecePresence & ~(0b_1U << bitPosition); // remove piece presence record
            }
            else
            {
                copy.piecePresence = piecePresence | 0b_1U << bitPosition;  // set piece presence record

                copy.pieceRanks = pieceRanks | (((uint)pieceType & 0b_10U) >> 1) << bitPosition;  // set piece rank
                copy.pieceColors = pieceColors | ((uint)pieceType & 0b_01U) << bitPosition;  // set piece color
            }

            return copy;
        }

        /// <summary>
        /// Returns new state with applied move to the current state
        /// </summary>
        /// <param name="move"></param>
        /// <returns></returns>
        public BoardState ApplyMove(Move move) 
        { 
            var copy = this;

            Debug.Assert(move.path != null, "move.path can not be null");

            Debug.Assert(move.path.Length >= 2, "path length must be at least 2 long");

            var playerColor = GetColor(GetPieceType(move.path.First()));
            Debug.Assert(playerColor == OnMove, $"{Enum.GetName(typeof(PieceColor), playerColor)} is not on move");

            Debug.Assert(!(from pos in move.path.Skip(1)
                           where copy.GetPieceType(pos) != PieceType.None
                           select pos).Any(), "can not jump to non-empty position");

            Debug.Assert(!(move.positionsOfTakenPieces != null
                         && (from pos in move.positionsOfTakenPieces
                             let pieceT = copy.GetPieceType(pos)
                             where pieceT == PieceType.None || GetColor(pieceT) == playerColor
                             select pos).Any()), "can not take empty place, or your own piece");
            

            var pieceType = GetPieceType(move.path.First());
            if (move.promotion != -1)
            {
                pieceType = PromoteToKing(pieceType);
            }

            // remove piece from start position
            copy = copy.SetPiece(move.path.Last(), pieceType);
            copy = copy.SetPiece(move.path.First(), PieceType.None);


            // remove taken pieces
            if (move.positionsOfTakenPieces != null)
            {
                foreach (var pos in move.positionsOfTakenPieces)
                {
                    copy = copy.SetPiece(pos, PieceType.None);
                }
            }

            copy.OnMove = SwapColor(OnMove);

            return copy;
        }

        public IEnumerable<(Position pos, PieceType pieceType)> IterateBoard()
        {
            for (byte column = 0; column < NumberOfColumns; column++)
            {
                for (byte row = 0; row < NumberOfRows; row++)
                {
                    var pos = new Position(column, row);
                    yield return (pos, GetPieceType(pos));
                }
            }
        }



        public override bool Equals(object obj)
        {
            return obj is BoardState bs
                ? piecePresence == bs.piecePresence
                  && pieceRanks == bs.pieceRanks
                  && pieceColors == bs.pieceColors
                  && OnMove == bs.OnMove
                  && rules.rulesType == bs.rules.rulesType
                : false;
        }

        public static bool operator ==(BoardState bs0, BoardState bs1)
        {
            return bs0.Equals(bs1);
        }
        public static bool operator !=(BoardState bs0, BoardState bs1)
        {
            return !bs0.Equals(bs1);
        }

        public override int GetHashCode()
        {
            ulong mask = 0b_11111111_11111111_11111111_11111111;
            return
                (int)(piecePresence & mask) ^ (int)(piecePresence >> 32)
              ^ (int)(pieceRanks    & mask) ^ (int)(pieceRanks    >> 32)
              ^ (int)(pieceColors   & mask) ^ (int)(pieceColors   >> 32);
        }
    }
}