/* Demo data. Replace with API calls in production. Prices are base KWD. */
const RATE = { KWD: 1, AED: 12.05 };           // demo conversion rate
const CATS = [
  { id:'investing',  en:'Investing',           ar:'الاستثمار',              hue:'#0B5D4A' },
  { id:'trading',    en:'Trading & markets',   ar:'التداول والأسواق',        hue:'#7A2E3B' },
  { id:'personal',   en:'Personal finance',    ar:'التمويل الشخصي',          hue:'#1F4B7A' },
  { id:'islamic',    en:'Islamic finance',     ar:'التمويل الإسلامي',        hue:'#2F5D2A' },
  { id:'accounting', en:'Accounting',          ar:'المحاسبة',                hue:'#5A4A22' },
  { id:'business',   en:'Business & startups', ar:'الأعمال وريادة الأعمال',  hue:'#3C3550' }
];
const BOOKS = [
  { id:1,  cat:'investing', price:6.500, pages:214, year:2025, sales:412, en:{t:'The Patient Portfolio', a:'Nadia Al-Rashed', d:'A case against trading your savings. Builds a ten-year allocation you can leave alone, with worked examples from Gulf and US markets.'}, ar:{t:'المحفظة الصبورة', a:'نادية الراشد', d:'حجة ضد المتاجرة بمدخراتك. يبني توزيعاً استثمارياً لعشر سنوات يمكنك تركه دون تدخل، مع أمثلة من أسواق الخليج وأمريكا.'} },
  { id:2,  cat:'investing', price:7.750, pages:288, year:2024, sales:301, en:{t:'Read the Balance Sheet First', a:'Youssef Haddad', d:'Twelve financial statements pulled apart line by line, showing what to check before you put money into a listed company.'}, ar:{t:'اقرأ الميزانية أولاً', a:'يوسف حداد', d:'اثنتا عشرة قائمة مالية مفككة سطراً بسطر، تبيّن ما يجب فحصه قبل استثمار أموالك في شركة مدرجة.'} },
  { id:3,  cat:'investing', price:5.250, pages:176, year:2025, sales:266, en:{t:'Dividends in Gulf Markets', a:'Mariam Al-Fadhli', d:'How payout policies work on Boursa Kuwait and the DFM, and how to tell a sustainable dividend from a shrinking one.'}, ar:{t:'التوزيعات في أسواق الخليج', a:'مريم الفضلي', d:'كيف تعمل سياسات التوزيع في بورصة الكويت وسوق دبي المالي، وكيف تميّز التوزيع المستدام من المتناقص.'} },
  { id:4,  cat:'investing', price:3.500, pages:132, year:2024, sales:520, en:{t:'Index Funds, Explained Simply', a:'Omar Sabbagh', d:'What an index fund actually holds, what it costs you each year, and how to open one from Kuwait or the UAE.'}, ar:{t:'صناديق المؤشرات ببساطة', a:'عمر صباغ', d:'ماذا يملك صندوق المؤشر فعلياً، وكم يكلفك سنوياً، وكيف تفتح حساباً من الكويت أو الإمارات.'} },
  { id:5,  cat:'trading', price:8.250, pages:246, year:2025, sales:198, en:{t:'Risk First, Profit Second', a:'Khalid Al-Mutairi', d:'Position sizing, stop placement and drawdown limits written for people trading their own capital, not a desk.'}, ar:{t:'المخاطرة أولاً والربح ثانياً', a:'خالد المطيري', d:'حجم المركز ووضع وقف الخسارة وحدود التراجع، مكتوبة لمن يتداول برأس ماله الخاص لا لمكتب مؤسسي.'} },
  { id:6,  cat:'trading', price:6.000, pages:190, year:2024, sales:224, en:{t:'Candlesticks Without the Myths', a:'Dana Kanaan', d:'Which candle patterns survive testing, which do not, and why most published win rates are measured wrong.'}, ar:{t:'الشموع اليابانية بلا أساطير', a:'دانة كنعان', d:'أي أنماط الشموع تصمد أمام الاختبار وأيها لا، ولماذا تُقاس نسب النجاح المنشورة بطريقة خاطئة.'} },
  { id:7,  cat:'trading', price:4.750, pages:148, year:2025, sales:157, en:{t:'Building a Trading Journal', a:'Faisal Darwish', d:'A journal template and a review routine that turns six months of trades into a decision you can defend.'}, ar:{t:'كيف تبني دفتر تداول', a:'فيصل درويش', d:'قالب دفتر وروتين مراجعة يحوّل ستة أشهر من الصفقات إلى قرار يمكنك الدفاع عنه.'} },
  { id:8,  cat:'personal', price:2.500, pages:98,  year:2025, sales:734, en:{t:'Your First Emergency Fund', a:'Layla Al-Sane', d:'How much to hold, where to keep it in KWD or AED, and the order to rebuild it after you spend it.'}, ar:{t:'صندوق الطوارئ الأول', a:'ليلى الصانع', d:'كم تحتفظ، وأين تحفظه بالدينار أو الدرهم، وبأي ترتيب تعيد بناءه بعد إنفاقه.'} },
  { id:9,  cat:'personal', price:4.250, pages:164, year:2024, sales:489, en:{t:'Paying Off Debt in 18 Months', a:'Hassan Al-Qattan', d:'A repayment plan built around salary dates and consolidation rules used by Kuwaiti and UAE banks.'}, ar:{t:'سداد الديون في 18 شهراً', a:'حسن القطان', d:'خطة سداد مبنية على مواعيد الراتب وقواعد دمج القروض في بنوك الكويت والإمارات.'} },
  { id:10, cat:'personal', price:3.750, pages:142, year:2025, sales:352, en:{t:'A Household Budget That Holds', a:'Reem Al-Ansari', d:'Budgets fail in month three. This one plans for the school fees, the travel and the two heavy months.'}, ar:{t:'ميزانية منزلية تصمد', a:'ريم الأنصاري', d:'الميزانيات تفشل في الشهر الثالث. هذه تخطط للرسوم الدراسية والسفر والشهرين الثقيلين.'} },
  { id:11, cat:'personal', price:3.000, pages:110, year:2024, sales:295, en:{t:'Salary to Savings', a:'Mohammed Barakat', d:'Automating the transfer on payday, and what to do when your income is irregular or commission-based.'}, ar:{t:'من الراتب إلى المدخرات', a:'محمد بركات', d:'أتمتة التحويل يوم الراتب، وماذا تفعل عندما يكون دخلك غير منتظم أو قائماً على العمولة.'} },
  { id:12, cat:'islamic', price:7.000, pages:232, year:2025, sales:263, en:{t:'Murabaha in Plain Language', a:'Dr. Abdullah Al-Harbi', d:'The contract, the cost-plus calculation and the common objections, without the legal shorthand.'}, ar:{t:'المرابحة بلغة واضحة', a:'د. عبدالله الحربي', d:'العقد وحساب التكلفة زائد الربح والاعتراضات الشائعة، دون اختصارات قانونية.'} },
  { id:13, cat:'islamic', price:5.500, pages:186, year:2024, sales:211, en:{t:'Sukuk for Beginners', a:'Sara Al-Jassim', d:'How a sukuk is structured, what the investor actually owns, and how returns differ from a conventional bond.'}, ar:{t:'الصكوك للمبتدئين', a:'سارة الجاسم', d:'كيف تُهيكل الصكوك، وماذا يملك المستثمر فعلياً، وكيف تختلف العوائد عن السند التقليدي.'} },
  { id:14, cat:'islamic', price:6.250, pages:204, year:2025, sales:176, en:{t:'Screening Shariah-Compliant Stocks', a:'Tariq Bin Nasser', d:'The debt and income ratios used by the main screening boards, applied to real Gulf listings.'}, ar:{t:'تصفية الأسهم المتوافقة مع الشريعة', a:'طارق بن ناصر', d:'نسب الدين والدخل المعتمدة لدى هيئات التصفية الرئيسية، مطبقة على شركات خليجية مدرجة.'} },
  { id:15, cat:'accounting', price:5.750, pages:198, year:2024, sales:187, en:{t:'Bookkeeping for Small Shops', a:'Noura Al-Enezi', d:'A monthly closing routine for a business with one owner, one bank account and no accountant on staff.'}, ar:{t:'مسك الدفاتر للمحلات الصغيرة', a:'نورة العنزي', d:'روتين إقفال شهري لمنشأة بمالك واحد وحساب بنكي واحد وبلا محاسب متفرغ.'} },
  { id:16, cat:'accounting', price:6.750, pages:226, year:2025, sales:144, en:{t:'Cash Flow, Line by Line', a:'Ahmed Fathi', d:'Why profitable businesses run out of money, shown through eighteen months of a real trading company.'}, ar:{t:'التدفق النقدي سطراً بسطر', a:'أحمد فتحي', d:'لماذا تنفد السيولة من شركات رابحة، من خلال ثمانية عشر شهراً من شركة تجارية حقيقية.'} },
  { id:17, cat:'accounting', price:9.750, pages:310, year:2025, sales:132, en:{t:'VAT and the GCC Retailer', a:'Huda Salem', d:'Registration thresholds, invoice rules and filing calendars across the GCC states that have implemented VAT.'}, ar:{t:'ضريبة القيمة المضافة وتاجر التجزئة', a:'هدى سالم', d:'حدود التسجيل وقواعد الفواتير ومواعيد الإقرارات في دول الخليج المطبِّقة للضريبة.'} },
  { id:18, cat:'business', price:4.500, pages:158, year:2025, sales:398, en:{t:'Launching on a Kuwaiti Budget', a:'Bader Al-Otaibi', d:'Licensing, a lean first-year cost sheet, and the twelve expenses first-time owners forget to plan for.'}, ar:{t:'إطلاق مشروعك بميزانية كويتية', a:'بدر العتيبي', d:'التراخيص وجدول تكاليف السنة الأولى، والاثنا عشر مصروفاً التي ينساها أصحاب المشاريع الجدد.'} },
  { id:19, cat:'business', price:5.000, pages:172, year:2024, sales:276, en:{t:'Pricing Your First Product', a:'Zainab Murad', d:'Cost-plus, value-based and competitive pricing compared, then applied to a small Gulf retail catalogue.'}, ar:{t:'تسعير منتجك الأول', a:'زينب مراد', d:'مقارنة بين التسعير بالتكلفة والقيمة والمنافسة، ثم تطبيقها على كتالوج تجزئة خليجي صغير.'} },
  { id:20, cat:'business', price:6.250, pages:208, year:2025, sales:241, en:{t:'From Instagram Shop to Company', a:'Yaser Al-Kandari', d:'Moving an account-based business onto a licence, a payment gateway and books that survive an audit.'}, ar:{t:'من متجر إنستغرام إلى شركة', a:'ياسر الكندري', d:'نقل نشاط قائم على حساب إلى ترخيص وبوابة دفع ودفاتر تصمد أمام التدقيق.'} }
];
const ORDERS = [
  { id:'KW-10428', cust:'Fatima A.', email:'fatima.a@email.com', items:[1,8], total:9.000, cur:'KWD', pay:'chg_TS02A1120231', status:'paid',     date:'2026-09-06 14:22' },
  { id:'AE-10427', cust:'Rashid M.', email:'rashid.m@email.com', items:[5],   total:99.40, cur:'AED', pay:'chg_TS02A1120198', status:'paid',     date:'2026-09-06 11:05' },
  { id:'KW-10426', cust:'Dalal S.',  email:'dalal.s@email.com',  items:[12,13,14], total:18.750, cur:'KWD', pay:'chg_TS02A1120177', status:'paid', date:'2026-09-05 20:48' },
  { id:'KW-10425', cust:'Ali H.',    email:'ali.h@email.com',    items:[17],  total:9.750, cur:'KWD', pay:'—',                status:'failed',   date:'2026-09-05 18:30' },
  { id:'AE-10424', cust:'Mona K.',   email:'mona.k@email.com',   items:[4,10],total:87.36, cur:'AED', pay:'chg_TS02A1120145', status:'paid',     date:'2026-09-05 09:12' },
  { id:'KW-10423', cust:'Yousef B.', email:'yousef.b@email.com', items:[2],   total:7.750, cur:'KWD', pay:'chg_TS02A1120121', status:'refunded', date:'2026-09-04 16:59' },
  { id:'KW-10422', cust:'Hessa N.',  email:'hessa.n@email.com',  items:[8,9,10,11], total:13.500, cur:'KWD', pay:'chg_TS02A1120090', status:'paid', date:'2026-09-04 08:20' }
];
const CUSTOMERS = [
  { name:'Fatima A.', email:'fatima.a@email.com', country:'Kuwait',  orders:4, spent:31.250, joined:'2025-11-02' },
  { name:'Rashid M.', email:'rashid.m@email.com', country:'UAE',     orders:2, spent:14.250, joined:'2026-01-18' },
  { name:'Dalal S.',  email:'dalal.s@email.com',  country:'Kuwait',  orders:6, spent:48.500, joined:'2025-08-27' },
  { name:'Ali H.',    email:'ali.h@email.com',    country:'Kuwait',  orders:1, spent:0.000,  joined:'2026-09-05' },
  { name:'Mona K.',   email:'mona.k@email.com',   country:'UAE',     orders:3, spent:21.750, joined:'2026-03-11' },
  { name:'Yousef B.', email:'yousef.b@email.com', country:'Kuwait',  orders:2, spent:7.750,  joined:'2026-02-04' },
  { name:'Hessa N.',  email:'hessa.n@email.com',  country:'Kuwait',  orders:5, spent:39.000, joined:'2025-12-15' }
];
const DOWNLOADS = [
  { order:'KW-10428', book:1,  email:'fatima.a@email.com', when:'2026-09-06 14:23', ip:'168.187.4.22',  used:1, max:3, state:'active' },
  { order:'KW-10428', book:8,  email:'fatima.a@email.com', when:'2026-09-06 14:24', ip:'168.187.4.22',  used:2, max:3, state:'active' },
  { order:'AE-10427', book:5,  email:'rashid.m@email.com', when:'2026-09-06 11:07', ip:'94.200.18.140', used:1, max:3, state:'active' },
  { order:'KW-10426', book:12, email:'dalal.s@email.com',  when:'2026-09-05 20:50', ip:'37.36.112.9',   used:3, max:3, state:'exhausted' },
  { order:'KW-10426', book:13, email:'dalal.s@email.com',  when:'2026-09-05 20:52', ip:'37.36.112.9',   used:1, max:3, state:'active' },
  { order:'KW-10422', book:9,  email:'hessa.n@email.com',  when:'2026-09-04 08:22', ip:'168.187.60.7',  used:1, max:3, state:'expired' },
  { order:'AE-10424', book:4,  email:'mona.k@email.com',   when:'2026-09-05 09:15', ip:'94.204.77.3',   used:2, max:3, state:'active' }
];
const SALES_30D = [7,4,9,12,6,3,8,14,11,9,5,7,16,13,10,6,4,9,12,15,11,8,6,10,13,17,12,9,7,14];
