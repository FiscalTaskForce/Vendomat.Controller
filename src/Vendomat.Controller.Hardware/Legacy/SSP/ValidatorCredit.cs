using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Vendomat.Common.SSP
{
    public class ValidatorCredit
    {
        public string Name { get; set; }
        public decimal Amount { get; set; }
    }
    public class ValidatorResponse
    {
        public string Name { get; set; }
        public decimal Amount { get; set; }
        PollResponse ResponseType { get; set; }
    }
    public enum PollResponse
    {
        SSP_POLL_SLAVE_RESET = 0,
        SSP_POLL_READ_NOTE = 1,
        SSP_POLL_CREDIT_NOTE = 2,
        SSP_POLL_NOTE_REJECTING = 3,
        SSP_POLL_NOTE_REJECTED = 4,
        SSP_POLL_NOTE_STACKING = 5,
        SSP_POLL_NOTE_STACKED = 6,
        SSP_POLL_SAFE_NOTE_JAM = 7,
        SSP_POLL_UNSAFE_NOTE_JAM = 8,
        SSP_POLL_DISABLED = 9,
        SSP_POLL_FRAUD_ATTEMPT = 10,
        SSP_POLL_STACKER_FULL = 11,
        SSP_POLL_NOTE_CLEARED_FROM_FRONT = 12,
        SSP_POLL_NOTE_CLEARED_TO_CASHBOX = 13,
        SSP_POLL_CASHBOX_REMOVED = 14,
        SSP_POLL_CASHBOX_REPLACED = 15,
        SSP_POLL_NOTE_PATH_OPEN = 16,
        SSP_POLL_CHANNEL_DISABLE = 17,
        UNRECOGNISED_POLL = 18,
        SSP_POLL_ESCROW_HOLD = 19,
    }
}
