/**
 * The accessibility statement template (GA10, PRD §4.14; European Accessibility Act, Directive (EU) 2019/882).
 *
 * The host copies it into a `LegalDocument` of kind `accessibility-statement`, fills the `{placeholders}` and publishes
 * it; the storefront's `/legal/accessibility` page shows the published version. The library never shows the template
 * itself: unfilled placeholders on a public page would be worse than no statement (startup warns when none is
 * published; microenterprises providing services are exempt, so it is a warning, not a refusal).
 *
 * ⚠️ D37, unconfirmed: the structure follows the PRD's reading of Annex V (a general description of the service, the
 * explanations needed to use it, how the Annex I requirements are met) and of Art. 13 (information in the general terms,
 * or another clearly visible way). The EUR-Lex text and the national transposition (for the enforcement authority to
 * name) were **not** re-read for this template. The WCAG 2.2.1 reasoning follows the normative SC text; W3C's
 * *Understanding SC 2.2.1* was **not** re-read either. Have both checked before relying on the wording.
 *
 * Placeholders: `{organisation}`, `{service}`, `{contact}`, `{date}`, `{authority}`, `{holdDefault}`, `{holdExtension}`,
 * `{holdMax}`.
 */
export const SPARK_ACCESSIBILITY_STATEMENT_TEMPLATE: Readonly<Record<'en' | 'fr' | 'nl', string>> = {
  en: `Accessibility statement — {service}

{organisation} wants {service} to be usable by everyone. This statement describes the service, how to use it, and how it meets the accessibility requirements of the European Accessibility Act (Directive (EU) 2019/882).

1. The service
{service} lets you browse products and events, put them in a basket, pay, and manage your orders, tickets and personal settings online.

2. How to use it
- Every page works with a keyboard alone. A "Skip to content" link is the first item on each page.
- Seats can be chosen from a list instead of the seat map: pick a block and a number of seats and the best available seats are reserved.
- Times are shown as text in your own time zone, with the venue's local time beside them.
- Messages about your basket and reservations are announced to screen readers without interrupting you.

3. Time limits
When you put scarce items such as seats in your basket, they are reserved for you for {holdDefault}. The deadline is shown as text. Five minutes before it ends, a "More time" button appears; each press adds {holdExtension}, up to {holdMax} in total, and you are warned at 5 minutes, 1 minute and 20 seconds. Beyond {holdMax} the reservation cannot be extended: it holds stock that other buyers are waiting for at the same moment, so the time limit is part of a real-time event and is essential to a fair sale (WCAG 2.2, success criterion 2.2.1, real-time and essential exceptions). In a waiting room, your place is kept: if you miss a presence check you rejoin at your previous position.

4. How we meet the requirements
The service is designed and tested against WCAG 2.1 level AA (the harmonised standard EN 301 549). Automated checks run on the shop, basket, checkout, waiting room, seat list, withdrawal function and consent banner. Known limitations: {limitations}

5. Contact and complaints
If something does not work for you, tell us at {contact}; we will answer and offer the information in another way. If you are not satisfied with our answer, you can contact {authority}.

This statement was last reviewed on {date}.`,
  fr: `Déclaration d'accessibilité — {service}

{organisation} veut que {service} soit utilisable par tous. Cette déclaration décrit le service, la manière de l'utiliser et la façon dont il répond aux exigences d'accessibilité de l'Acte européen sur l'accessibilité (directive (UE) 2019/882).

1. Le service
{service} vous permet de parcourir des produits et des événements, de les mettre dans un panier, de payer et de gérer en ligne vos commandes, vos billets et vos paramètres personnels.

2. Utilisation
- Chaque page fonctionne entièrement au clavier. Un lien « Aller au contenu » est le premier élément de chaque page.
- Les places peuvent être choisies dans une liste plutôt que sur le plan de salle : choisissez un bloc et un nombre de places, et les meilleures places disponibles sont réservées.
- Les heures sont affichées en texte dans votre fuseau horaire, avec l'heure locale du lieu à côté.
- Les messages concernant votre panier et vos réservations sont annoncés aux lecteurs d'écran sans vous interrompre.

3. Délais
Lorsque vous mettez des articles rares, comme des places, dans votre panier, ils vous sont réservés pendant {holdDefault}. L'échéance est affichée en texte. Cinq minutes avant la fin, un bouton « Plus de temps » apparaît ; chaque pression ajoute {holdExtension}, jusqu'à {holdMax} au total, et vous êtes averti à 5 minutes, 1 minute et 20 secondes. Au-delà de {holdMax}, la réservation ne peut plus être prolongée : elle bloque un stock que d'autres acheteurs attendent au même moment ; le délai fait donc partie d'un événement en temps réel et est essentiel à une vente équitable (WCAG 2.2, critère 2.2.1, exceptions « temps réel » et « essentiel »). Dans une file d'attente, votre place est conservée : si vous manquez un contrôle de présence, vous reprenez votre position précédente.

4. Conformité
Le service est conçu et testé selon les WCAG 2.1 niveau AA (norme harmonisée EN 301 549). Des contrôles automatiques portent sur la boutique, le panier, la commande, la file d'attente, la liste des places, la fonction de rétractation et le bandeau de consentement. Limites connues : {limitations}

5. Contact et plaintes
Si quelque chose ne fonctionne pas pour vous, signalez-le à {contact} ; nous vous répondrons et vous proposerons l'information sous une autre forme. Si notre réponse ne vous satisfait pas, vous pouvez vous adresser à {authority}.

Cette déclaration a été revue pour la dernière fois le {date}.`,
  nl: `Toegankelijkheidsverklaring — {service}

{organisation} wil dat iedereen {service} kan gebruiken. Deze verklaring beschrijft de dienst, hoe je ze gebruikt en hoe ze voldoet aan de toegankelijkheidseisen van de Europese toegankelijkheidswet (Richtlijn (EU) 2019/882).

1. De dienst
Met {service} bekijk je producten en evenementen, leg je ze in een winkelmandje, betaal je en beheer je online je bestellingen, tickets en persoonlijke instellingen.

2. Gebruik
- Elke pagina werkt volledig met het toetsenbord. Een link "Naar de inhoud" is het eerste element op elke pagina.
- Plaatsen kies je ook uit een lijst in plaats van op het zaalplan: kies een blok en een aantal plaatsen, en de beste vrije plaatsen worden voor je gereserveerd.
- Tijden staan als tekst in je eigen tijdzone, met de lokale tijd van de locatie ernaast.
- Meldingen over je winkelmandje en reserveringen worden aan schermlezers voorgelezen zonder je te onderbreken.

3. Tijdslimieten
Leg je schaarse artikelen zoals plaatsen in je winkelmandje, dan worden ze {holdDefault} voor je gereserveerd. Het tijdstip waarop de reservering afloopt, staat als tekst. Vijf minuten voor het einde verschijnt een knop "Meer tijd"; elke druk voegt {holdExtension} toe, tot {holdMax} in totaal, en je krijgt een melding na 5 minuten, 1 minuut en 20 seconden. Na {holdMax} kan de reservering niet meer verlengd worden: ze houdt voorraad vast waar andere kopers op hetzelfde moment op wachten. De tijdslimiet hoort dus bij een gebeurtenis in realtime en is essentieel voor een eerlijke verkoop (WCAG 2.2, succescriterium 2.2.1, uitzonderingen "realtime" en "essentieel"). In een wachtrij blijft je plaats behouden: mis je een aanwezigheidscontrole, dan kom je terug op je vorige plaats.

4. Hoe we aan de eisen voldoen
De dienst is ontworpen en getest volgens WCAG 2.1 niveau AA (de geharmoniseerde norm EN 301 549). Automatische controles lopen op de winkel, het winkelmandje, het afrekenen, de wachtrij, de plaatsenlijst, de herroepingsfunctie en de toestemmingsbanner. Bekende beperkingen: {limitations}

5. Contact en klachten
Werkt iets niet voor jou, meld het dan via {contact}; we antwoorden en bieden de informatie op een andere manier aan. Ben je niet tevreden met ons antwoord, dan kun je terecht bij {authority}.

Deze verklaring werd het laatst herzien op {date}.`,
};

/** The template's placeholders, for a host's fill-in form. */
export const SPARK_ACCESSIBILITY_STATEMENT_PLACEHOLDERS: readonly string[] = [
  'organisation', 'service', 'contact', 'date', 'authority', 'holdDefault', 'holdExtension', 'holdMax', 'limitations',
];

/** The template with `values` filled in; placeholders without a value are left as they are (pure). */
export function fillAccessibilityStatement(language: 'en' | 'fr' | 'nl', values: Partial<Record<string, string>>): string {
  return SPARK_ACCESSIBILITY_STATEMENT_TEMPLATE[language].replace(/\{([a-zA-Z]+)\}/g, (all, name: string) => values[name] ?? all);
}

/** The placeholders still in `text` (a host's check before publishing). */
export function unfilledPlaceholders(text: string): string[] {
  return [...new Set([...text.matchAll(/\{([a-zA-Z]+)\}/g)].map((m) => m[1]))].filter((n) => SPARK_ACCESSIBILITY_STATEMENT_PLACEHOLDERS.includes(n));
}
