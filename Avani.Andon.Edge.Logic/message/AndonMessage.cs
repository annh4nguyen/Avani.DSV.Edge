﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Avani.Andon.Edge.Logic
{
    public class AndonMessage
    {
        public string PackageRequest(int node)
        {
            try
            {
                int command = 3;
                int startHi = 0;
                int startLo = 1;
                int pointHi = 0;
                int pointLo = 6;
                int check = ((node + command + startHi + startLo + pointHi + pointLo) ^ 255) + 1;
                string message = string.Format(":{0:X2}{1:X2}{2:X2}{3:X2}{4:X2}{5:X2}{6:X2}\r\n", node, command, startHi, startLo, pointHi, pointLo, check);

                return message;
                //return Encoding.ASCII.GetBytes(message);
            }
            catch (Exception ex)
            {
                //log.Error(ex.Message);
                return "";
            }
        }

        public string PackageWrite(int node, int register, int value)
        {
            try
            {
                int command = 6;
                int startHi = 0;
                int startLo = register;
                int pointHi = 0;
                int pointLo = value;
                int check = ((node + command + startHi + startLo + pointHi + pointLo) ^ 255) + 1;
                string message = string.Format(":{0:X2}{1:X2}{2:X2}{3:X2}{4:X2}{5:X2}", node, command, startHi, startLo, pointHi, pointLo);

                return message;
                //return Encoding.ASCII.GetBytes(message);
            }
            catch (Exception ex)
            {
                //log.Error(ex.Message);
                return "";
            }
        }

        public string PackageMessageNewFormat(string strMessage, byte NumberOfRegister)
        {
            string temp = "";
            try
            {



                int GroupId = 1;
                int NodeId = Convert.ToInt32(strMessage.Substring(0, 2), 16);
                temp = string.Format(":D{0:D2}{1:D3}03", GroupId, NodeId);

                int[] DataLo = new int[6];

                int Command = Convert.ToInt32(strMessage.Substring(2, 2), 16);

                int ByteCount = Convert.ToInt32(strMessage.Substring(4, 2), 16);

                for (int i = 0; i < NumberOfRegister; i++)
                {
                    DataLo[i] = Convert.ToInt32(strMessage.Substring(4 + (i + 1) * 4, 2), 16);
                    temp += string.Format("{0:D2}", DataLo[i]);
                }
                for (int i = NumberOfRegister; i < 10; i++)
                {
                    temp += string.Format("{0:D2}", 0);
                }
                int LRCCheck = Convert.ToInt32(strMessage.Substring(6 + NumberOfRegister * 4, 2), 16);


                int check = 1 + NodeId + Command;
                for (int i = 0; i < NumberOfRegister; i++)
                    check += DataLo[i]; //Chỉ thêm DataLow, không cần tính đến DataHi

                check = (check ^ 255) + 1;

                temp += string.Format("{0:d3}\r\n", check);

                //if (NodeId == 16)
                //{
                //    log.Info("NodeId: " + NodeId + "; Status: "+ ret);
                //}
            }
            catch (Exception ex)
            {
                //ret = false;
                //log.Error(ex.Message);
            }

            return temp;
        }

        /// <summary>
        /// Package Stop Message
        /// </summary>
        public string PackageSetValueMessage(int GroupId, int Id, int RegisterNo, int value)
        {
            string temp = "";
            try
            {

                int check = GroupId + Id + RegisterNo + value;

                check = (check ^ 255) + 1;

                temp = string.Format(":S{0:D2}{1:D3}05{2:D2}{3:D2}{4:D3}\r\n", GroupId, Id, RegisterNo, value, check);
            }
            catch (Exception ex)
            {
                //ret = false;
                //log.Error(ex.Message);
            }

            return temp;
        }

        public string PackageSetValueMessage(int GroupId, int Id, int in01, int in02, int in03, int in04, int in05, int in06)
        {
            string temp = "";
            try
            {
                int command = 2;
                int check = GroupId + Id + command + in01 + in02 + in03 + in04 + in05 + in06;

                check = (check ^ 255) + 1;

                temp = string.Format(":S{0:D2}{1:D3}{2:D2}{3:D2}{4:D2}{5:D2}{6:D2}{7:D2}{8:D2}{9:D3}\r\n", GroupId, Id, command, in01, in02, in03, in04, in05, in06, check);
            }
            catch (Exception ex)
            {
                //ret = false;
                //log.Error(ex.Message);
            }

            return temp;
        }

    }
}
