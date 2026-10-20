/* Demo rows the control panel still renders: orders, customers, the
 * downloads log and the sales sparkline. They are NOT book data - the
 * catalogue now comes from /api (see api.js).
 *
 * This file exists so the admin shell keeps working until Phase 7 replaces it
 * with the real control panel. Loaded only by pages under /admin.
 */
const ORDERS = [
  {
    "id": "KW-10428",
    "cust": "Fatima A.",
    "email": "fatima.a@email.com",
    "items": [
      1,
      8
    ],
    "total": 9,
    "cur": "KWD",
    "pay": "chg_TS02A1120231",
    "status": "paid",
    "date": "2026-09-06 14:22"
  },
  {
    "id": "AE-10427",
    "cust": "Rashid M.",
    "email": "rashid.m@email.com",
    "items": [
      5
    ],
    "total": 99.4,
    "cur": "AED",
    "pay": "chg_TS02A1120198",
    "status": "paid",
    "date": "2026-09-06 11:05"
  },
  {
    "id": "KW-10426",
    "cust": "Dalal S.",
    "email": "dalal.s@email.com",
    "items": [
      12,
      13,
      14
    ],
    "total": 18.75,
    "cur": "KWD",
    "pay": "chg_TS02A1120177",
    "status": "paid",
    "date": "2026-09-05 20:48"
  },
  {
    "id": "KW-10425",
    "cust": "Ali H.",
    "email": "ali.h@email.com",
    "items": [
      17
    ],
    "total": 9.75,
    "cur": "KWD",
    "pay": "—",
    "status": "failed",
    "date": "2026-09-05 18:30"
  },
  {
    "id": "AE-10424",
    "cust": "Mona K.",
    "email": "mona.k@email.com",
    "items": [
      4,
      10
    ],
    "total": 87.36,
    "cur": "AED",
    "pay": "chg_TS02A1120145",
    "status": "paid",
    "date": "2026-09-05 09:12"
  },
  {
    "id": "KW-10423",
    "cust": "Yousef B.",
    "email": "yousef.b@email.com",
    "items": [
      2
    ],
    "total": 7.75,
    "cur": "KWD",
    "pay": "chg_TS02A1120121",
    "status": "refunded",
    "date": "2026-09-04 16:59"
  },
  {
    "id": "KW-10422",
    "cust": "Hessa N.",
    "email": "hessa.n@email.com",
    "items": [
      8,
      9,
      10,
      11
    ],
    "total": 13.5,
    "cur": "KWD",
    "pay": "chg_TS02A1120090",
    "status": "paid",
    "date": "2026-09-04 08:20"
  }
];
const CUSTOMERS = [
  {
    "name": "Fatima A.",
    "email": "fatima.a@email.com",
    "country": "Kuwait",
    "orders": 4,
    "spent": 31.25,
    "joined": "2025-11-02"
  },
  {
    "name": "Rashid M.",
    "email": "rashid.m@email.com",
    "country": "UAE",
    "orders": 2,
    "spent": 14.25,
    "joined": "2026-01-18"
  },
  {
    "name": "Dalal S.",
    "email": "dalal.s@email.com",
    "country": "Kuwait",
    "orders": 6,
    "spent": 48.5,
    "joined": "2025-08-27"
  },
  {
    "name": "Ali H.",
    "email": "ali.h@email.com",
    "country": "Kuwait",
    "orders": 1,
    "spent": 0,
    "joined": "2026-09-05"
  },
  {
    "name": "Mona K.",
    "email": "mona.k@email.com",
    "country": "UAE",
    "orders": 3,
    "spent": 21.75,
    "joined": "2026-03-11"
  },
  {
    "name": "Yousef B.",
    "email": "yousef.b@email.com",
    "country": "Kuwait",
    "orders": 2,
    "spent": 7.75,
    "joined": "2026-02-04"
  },
  {
    "name": "Hessa N.",
    "email": "hessa.n@email.com",
    "country": "Kuwait",
    "orders": 5,
    "spent": 39,
    "joined": "2025-12-15"
  }
];
const DOWNLOADS = [
  {
    "order": "KW-10428",
    "book": 1,
    "email": "fatima.a@email.com",
    "when": "2026-09-06 14:23",
    "ip": "168.187.4.22",
    "used": 1,
    "max": 3,
    "state": "active"
  },
  {
    "order": "KW-10428",
    "book": 8,
    "email": "fatima.a@email.com",
    "when": "2026-09-06 14:24",
    "ip": "168.187.4.22",
    "used": 2,
    "max": 3,
    "state": "active"
  },
  {
    "order": "AE-10427",
    "book": 5,
    "email": "rashid.m@email.com",
    "when": "2026-09-06 11:07",
    "ip": "94.200.18.140",
    "used": 1,
    "max": 3,
    "state": "active"
  },
  {
    "order": "KW-10426",
    "book": 12,
    "email": "dalal.s@email.com",
    "when": "2026-09-05 20:50",
    "ip": "37.36.112.9",
    "used": 3,
    "max": 3,
    "state": "exhausted"
  },
  {
    "order": "KW-10426",
    "book": 13,
    "email": "dalal.s@email.com",
    "when": "2026-09-05 20:52",
    "ip": "37.36.112.9",
    "used": 1,
    "max": 3,
    "state": "active"
  },
  {
    "order": "KW-10422",
    "book": 9,
    "email": "hessa.n@email.com",
    "when": "2026-09-04 08:22",
    "ip": "168.187.60.7",
    "used": 1,
    "max": 3,
    "state": "expired"
  },
  {
    "order": "AE-10424",
    "book": 4,
    "email": "mona.k@email.com",
    "when": "2026-09-05 09:15",
    "ip": "94.204.77.3",
    "used": 2,
    "max": 3,
    "state": "active"
  }
];
const SALES_30D = [7,4,9,12,6,3,8,14,11,9,5,7,16,13,10,6,4,9,12,15,11,8,6,10,13,17,12,9,7,14];
