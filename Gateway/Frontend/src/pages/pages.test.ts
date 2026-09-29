/**
 * What the gateway's own pages are held to: what every page of every kind
 * of node is, by the rules of WWCP_Node's test/pages.ts - none of them has
 * a form yet, which this says, and says again the day one has.
 *
 * Run with `npm test`. "@node/.." is WWCP_Node/Frontend, where the rules are.
 */

import { everyPageIn } from '@node/../test/pages.ts';


everyPageIn(new URL('./', import.meta.url), {
    withForms: []
});
