# Commerce compliance: what was checked against EUR-Lex

> ⚠️ **Not legal advice.** This page records which official text each legal rule of the commerce suite was checked
> against (decision D37), what the member states may decide differently, what is still open, and what stays the host's
> job. It does not replace the host's counsel.

All EU texts were read on **2026-10-20** on eur-lex.europa.eu in a real browser (EUR-Lex answers scripted HTTP clients
with `202` and an empty body). Where a consolidated version exists, the latest one was read. **National transpositions
(BE, NL, FR, LU, DE) were not checked**: every member-state option below is marked so.

## Texts read

| Act | Short name | CELEX read |
|---|---|---|
| Directive 2011/83/EU, as amended by 2019/2161, 2023/2673 and 2024/825 | CRD | `02011L0083-20260927` (+ `32011L0083` for recital 41) |
| Directive (EU) 2019/771, as amended by 2024/1799 | Sale of Goods (SGD) | `02019L0771-20260731` |
| Regulation (EU) 2023/988 | GPSR | `02023R0988-20260529` |
| Directive 98/6/EC, as amended by 2019/2161 | PID | `01998L0006-20220528` |
| Directive 2005/29/EC, as amended by 2019/2161 and 2024/825 | UCPD | `02005L0029-20260927` |
| Regulation (EU) 2019/1150 | P2B | `32019R1150` |
| Regulation (EU) 2022/2065 | DSA | `32022R2065` |
| Directive 2011/16/EU Annex V, inserted by Directive (EU) 2021/514 | DAC7 | `02011L0016-20260101`, `32021L0514` |
| Directive 2006/112/EC | VAT Directive | `02006L0112-20250414` |
| Implementing Regulation (EU) No 282/2011 | VAT implementing reg. | `02011R0282-20250414` |
| Directive (EU) 2019/882 | EAA | `32019L0882` |
| Directive (EU) 2024/825 | ECGT | `32024L0825` |
| Directive (EU) 2023/2673 | withdrawal function | `32023L2673` |
| Directive (EU) 2019/2161 | Omnibus | `32019L2161` |
| Implementing Regulation (EU) 2025/1960 | harmonised notice and label | `32025R1960` |
| Regulation (EEC, Euratom) No 1182/71 | periods and dates | `31971R1182` |
| Regulation (EU) 2016/679 | GDPR | `32016R0679` |
| Directive 2002/58/EC | ePrivacy | `02002L0058-20091219` |
| Regulation (EU) 2018/302 | geo-blocking | `32018R0302` |
| Directive (EU) 2015/2366 | PSD2 | `02015L2366-20250117` |
| Regulation (EU) No 952/2013 Art. 4 | Union Customs Code | `32013R0952` |
| Regulation (EU) 2019/1020 Art. 4 | market surveillance | `32019R1020` |
| W3C *Understanding SC 2.2.1 Timing Adjustable* (WCAG 2.2) | — | w3.org/WAI/WCAG22/Understanding/timing-adjustable |

Not read: the Commission's CRD guidance (2021/C 525/01), its PID guidance (2021/C 526/02), Recommendation 2003/361/EC,
the Windsor Framework, and every national text.

## Per item

Result: **OK** = the code follows the article; **fixed** = a citation or comment was wrong and is corrected;
**finding** = the code's rule differs from the article (listed under *Findings*; the logic was not changed).

| Item | Rule in the code | Article checked | Result | Member-state options (not checked) |
|---|---|---|---|---|
| GA1 | Withdrawal function: labelled button, form with name, contract and e-mail, "confirm withdrawal" step | CRD Art. 11a(1)–(3) (applies from 19 June 2026, 2023/2673 Art. 2(1)) | OK | — |
| GA1 | Acknowledgement on a durable medium with content, date and time | CRD Art. 11a(4) | **fixed** (was cited as 11a(3), the confirmation function) | — |
| GA1 | 14 days from the (last) good's receipt; lots and pieces from the last one | CRD Art. 9(1), 9(2)(b)(i)–(ii) | OK | Art. 9(1a): 30 days for unsolicited visits/excursions (not relevant online) |
| GA1 | Withdrawal possible before delivery | not in Art. 9's text (Art. 9(3) is about performing during the period) | guide **fixed** | — |
| GA1 | Day counting; weekends and holidays move the last day | Reg. 1182/71 Art. 3(1), 3(4); Art. 2(1); CRD recital 41 | OK, **finding F7** on whose holidays | holidays are national |
| GA1 | Refund within 14 days, same means of payment, delivery cost only up to the cheapest standard delivery, withholding until goods back or proof of dispatch | CRD Art. 13(1)–(3) | OK | — |
| GA1 | Buyer bears direct return cost unless seller agreed or failed to inform; diminished value | CRD Art. 14(1)–(2), Art. 6(1)(i) | OK | — |
| GA1 | Exemptions: 16(a), (c), (d), (e), (l), (m) | CRD Art. 16 | OK | last paragraph: derogations for unsolicited visits only |
| GA1 | Undeliverable units refunded with delivery cost (D45) | CRD Art. 18(2)–(3) "all sums paid under the contract" | **fixed** (was also cited as Art. 13(1), the withdrawal rule) | Art. 18(4): other national remedies |
| GA1 | Risk passes on delivery | CRD Art. 20 | OK | — |
| GA2 | "Order with obligation to pay" button; information directly before ordering | CRD Art. 8(2) | OK, **finding F2** (now also Art. 6(1)(la)) | — |
| GA2 | Confirmation on a durable medium | CRD Art. 8(7) | OK | — |
| GA2 | Main characteristics, total price incl. taxes and delivery | CRD Art. 6(1)(a), (e); PID Art. 2(a) | OK | Art. 6(7): language requirements |
| GA2/GC4 | No pre-ticked extras (insurance) | CRD Art. 22 | **fixed** (UCPD Annex I point 29, cited before, is inertia selling) | — |
| GA3 | Prior price = lowest of ≥ 30 days before the reduction | PID Art. 6a(1)–(2) | OK | Art. 6a(3) perishables, 6a(4) newcomers, 6a(5) progressive: **finding F5** |
| GA3 | Unit price per kg, l, m, m², m³ or a customary unit | PID Art. 2(b), 3(1), 3(4) | **fixed** (the PID has no annex of units) | Art. 3(2), Art. 5 exemptions |
| GA4 | Place of supply: admission where the event is | VAT Dir. Art. 53, 54(1) | OK, **finding F8** (virtual attendance) | Art. 194 reverse charge |
| GA4 | Distance sales threshold €10,000, destination taxation, OSS | VAT Dir. Art. 33(a), 59c(1)–(3), 369a ff. | OK | Art. 59c(3) election is the supplier's |
| GA4 | Intra-Community supply needs a valid VAT id | VAT Dir. Art. 138(1)(b) as amended by 2018/1910 | OK | — |
| GA4 | Exports, B2B services, reverse charge mention | VAT Dir. Art. 44, 45, 146(1), 196, 226(11a) | OK, **finding F9** (Monaco) | — |
| GA4 | Unverified VAT id treated as non-taxable | Impl. Reg. 282/2011 Art. 18(1)–(2) | OK | — |
| GA4 | OSS corrections in a later return | Impl. Reg. 282/2011 Art. 61(1) | OK | — |
| GA4 | Simplified invoice for B2C receipts | VAT Dir. Art. 220a, 226b | OK as an option (≤ €100 unless the member state uses Art. 238) | Art. 220a, 238 |
| GA4 | Delivery charged follows the goods | VAT Dir. Art. 78(b) | OK | — |
| GA5 | Trader data (a)–(e), best-effort check, public page with (a), (d), (e) | DSA Art. 30(1), 30(2), 30(7) | OK | — |
| GA5 | Trader data deleted 6 months after the relationship | DSA Art. 30(5) | **fixed** (was cited as 30(3), the correction-and-suspension rule) | — |
| GA5 | Exemption switch for micro/small platforms | DSA Art. 29 | OK | — |
| GA5 | Notice: reasons, location, name and e-mail, good-faith statement; receipt confirmation | DSA Art. 16(1)–(2), 16(4) | OK | — |
| GA5 | Statement of reasons and its content | DSA Art. 17(1), 17(3) | OK | — |
| GA5 | Transparency figures | DSA Art. 15(1), 24 | OK | — |
| GA5 | Ranking parameters page; trader or not; "consumer rights do not apply" for non-traders | CRD Art. 6a(1)(a)–(c); P2B Art. 5 | OK (citation widened to (b)–(c)) | Art. 6a(2): extra national requirements |
| GA5 | Terms notice ≥ 15 days; statement of reasons; complaints; small-enterprise exemption | P2B Art. 3(2), 4, 11, 11(5) | OK | — |
| GA6 | DAC7 seller data, report per quarter (consideration, activities, fees) | Dir. 2011/16 Annex V Section II; III B(2)(e)–(f) | **fixed** (was "III B(2)(a)(iii)–(iv)") | — |
| GA6 | Excluded seller: < 30 sales of goods and ≤ €2,000 | Annex V Section I **B(4)(d)** | **fixed** (was "I A(4)(d)") | — |
| GA6 | "Personal service" | Annex V Section I **A(11)** | **fixed** (was "I A(9)") | — |
| GA6 | Two reminders, ≥ 60 days, then close or withhold | Annex V Section IV A(2) | **finding F6** | national enforcement |
| GA6 | Records kept 5–10 years | Annex V Section IV B(1) | OK (citation made precise) | period within 5–10 years |
| GA7 | Online offer shows manufacturer, EU responsible person, identifier and picture, warnings | GPSR Art. 19(a)–(d), 16(1); Reg. 2019/1020 Art. 4 | OK | 19(d): language set by the member state |
| GA7 | Recall: notify identifiable consumers directly | GPSR Art. 35(1), 36(2) | OK, **finding F4** (notice content) | — |
| GA8 | Legal guarantee ≥ 2 years from delivery | SGD Art. 10(1), 10(3) | OK, **finding F3** | Art. 10(3) longer periods, 10(5) limitation only, 10(6) second-hand ≥ 1 year |
| GA8 | Remedies, free of charge, seller takes goods back | SGD Art. 13, 14(1)–(2), 16 | OK | — |
| GA8 | Legal-guarantee notice and durability-guarantee label | CRD Art. 6(1)(l), **6(1)(la)**, 22a; Impl. Reg. 2025/1960 | **fixed** (was cited as 6(1)(m)); **finding F1** | — |
| GA9 | Cookie / device storage consent | ePrivacy Art. 5(3) | OK | — |
| GA9 | Marketing mail opt-in at checkout | ePrivacy Art. 13(1)–(2) | **fixed** (it is consent under 13(1), stricter than the 13(2) soft opt-in) | — |
| GA9 | Consent proof, withdrawal as easy as giving, objection to marketing | GDPR Art. 4(11), 7(1), 7(3), 21(2) | OK | — |
| GA9/GB3 | Sensitive attendee answers only with explicit consent | GDPR Art. 9(2)(a) | OK | Art. 9(4) national conditions |
| GA9 | Exports, erasure, its limits | GDPR Art. 15, 17, 17(3)(b), (e), 20 | OK | — |
| Challenges | Deceptive measures; automated refusal | UCPD Art. 5–7; GDPR Art. 22(3) | OK | — |
| GA10 | Accessibility information in the terms or an equivalent document; written and oral | EAA Annex V point 1, Art. 13(2); micro exemption Art. 4(5) | OK (comment corrected) | enforcement authority is national |
| GA10 | Hold timer: warning, ≥ 20 s, simple action; ticket hold as essential | WCAG 2.2 SC 2.2.1 and its Understanding document | OK | — |
| GA11 | Same access; no different conditions by nationality, residence, card country | Geo-blocking Reg. Art. 3, 4(1), 5(1)(a)–(c) | OK | — |
| GC5 | Say whether and how reviews are verified; never misrepresent reviews | UCPD Art. 7(6), Annex I points 23b, 23c | **fixed** (Art. 7(6) added; "Art. 7(2)" removed) | — |
| GB6 | Customs territory | UCC Art. 4 | **finding F9** (Monaco) | — |
| Payments | Collecting for sellers; commercial-agent exclusion | PSD2 Art. 3(b) | OK (guide quotes it) | licensing is national |
| Retention | Trader data, DAC7, prior-price window | DSA Art. 30(5); Annex V IV B(1); PID Art. 6a(2) | OK | invoices: VAT Dir. Art. 247(1) leaves it to the member state |

## Findings: the code's rule differs from the article

The logic was **not** changed. Each needs a decision.

- **F1 — harmonised notice is published, the default is not it.** CRD Art. 6(1)(l) (as amended, from 27 September 2026):
  "a reminder of the existence of the legal guarantee of conformity … using the harmonised notice referred to in Article
  22a". Implementing Regulation (EU) 2025/1960, Art. 3: "It shall apply from 27 September 2026"; Annex I note 1: "None of
  the elements of the harmonised notice can be edited." `ShopGuaranteeNotice` sends the library's own wording when the
  host sets nothing.
- **F2 — durability label missing at the last step and too short a guarantee accepted.** CRD Art. 8(2) as amended:
  "directly before the consumer places his order, of the information provided for in Article 6(1), points (a), (e),
  (la), (o) and (p)". Art. 6(1)(la): a guarantee "at no additional cost, covering the entire good and with a duration of
  more than two years". `SparkCheckoutLine` carries no durability information, and `ProductRules` accepts any
  `DurabilityGuaranteeMonths` ≥ 1.
- **F3 — guarantee period.** SGD Art. 10(1): "within two years of that time"; Art. 10(2a) (Directive (EU) 2024/1799):
  "Where … repair takes place as the remedy …, the liability period shall be extended once by 12 months." Checkout
  counts `PeriodDays = 730` from delivery (one day short across a 29 February) and refuses claims after it
  (`RefundService.GuaranteeEnd`, "periodEnded"), with no extension after a repair.
- **F4 — recall notice content.** GPSR Art. 36(2): the notice includes "(a) a headline consisting of the words 'Product
  safety recall'", "(b)(i) picture, name and brand", "(iii) information on when, where and by whom the product was sold",
  "(e) … the remedies available to consumers in accordance with Article 37", "(f) a free phone number or interactive
  online service" and "(g) encouragement to share". `FulfilmentRecallNotice` holds name, SKU, lot, an optional hazard,
  action and contact only; the rest depends on the host's mail template.
- **F5 — progressive reductions applied everywhere.** PID Art. 6a(5): "Member States **may** provide that, when the
  price reduction is progressively increased, the prior price is the price without the price reduction before the
  first application". `PromotionRules.SeriesStart` always keeps the first step's reference.
- **F6 — DAC7 sanction.** Annex V Section IV A(2): the operator "shall close the account of the Seller and prevent the
  Seller from re-registering on the Platform or withhold the payment of the Consideration". `Dac7ReminderJob` blocks new
  sales only (D43: payouts untouched), which is neither alternative as worded.
- **F7 — whose public holidays.** Reg. 1182/71 Art. 2(1): "'public holidays' means all days designated as such in the
  Member State … in which action is to be taken." The withdrawal is the consumer's act; Checkout applies one configured
  `Refunds:HolidayCountry` (documented as the seller's).
- **F8 — virtual events.** VAT Dir. Art. 53, second paragraph: "This Article shall not apply to admission to the events
  … where the attendance is virtual"; Art. 54(1), second subparagraph: streamed activities are taxed where the
  non-taxable person "is established, has his permanent address or usually resides". `AdmissionRule` always taxes where
  the event takes place; Ticketing has no notion of virtual attendance.
- **F9 — Monaco.** UCC Art. 4(2)(a): Monaco is "part of the customs territory of the Union"; VAT Dir. Art. 7(2):
  transactions with Monaco "are treated as transactions originating in or intended for France". Shipping's default
  customs territory and Invoicing's `EuMemberStates` both leave `MC` out, so a shipment to Monaco becomes an export.

## Still open

- Every member-state option above, and the national texts behind the retention floors (Belgian VAT Code art. 60 §3,
  Code of Economic Law art. III.86), the Belgian ticket-resale cap (Act of 30 July 2013) and the CNIL consent periods.
- The Commission guidance documents (CRD 2021/C 525/01, PID 2021/C 526/02), including how a partial withdrawal shares
  the delivery cost (not settled by CRD Art. 13).
- Northern Ireland (`XI`) under the Windsor Framework (GPSR responsible person).
- Whether ticket sales are a DAC7 "Relevant Activity" (Annex V Section I A(8), A(11)): left to the host's counsel
  (`Dac7:TicketSalesInScope`).

## The host's responsibilities

- Publish the legal documents: terms, privacy policy (GDPR Art. 13), withdrawal information and model form (CRD
  Art. 6(1)(h), Annex I(A)–(B)), platform terms for sellers (P2B Art. 3), cookie policy, and the **accessibility
  statement** (EAA Annex V; microenterprises providing services are exempt, Art. 4(5)).
- Replace the guarantee notice with the harmonised notice and label of Implementing Regulation (EU) 2025/1960 (F1).
- Set the national values: guarantee period, holiday country, retention periods, VAT options, DAC7 reporting state.
- Supply a recall mail template with every Art. 36(2) element (F4).
- Make the environmental and durability claims shown on products true and substantiated (UCPD as amended by the ECGT,
  from 27 September 2026); the library only displays what the seller enters.
- Confirm the DSA Art. 29 and P2B Art. 11(5) small-enterprise acknowledgements before switching those features off.
- Confirm payment-services licensing (PSD2 Art. 3(b)) before collecting money for other sellers.
