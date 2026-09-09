/**
 * The Cloud Imaging release this backend was built from.
 *
 * This placeholder is overwritten by `release-iac.yml` immediately before `npm run build`,
 * so the value is baked into the compiled output rather than supplied as configuration.
 * That matters: the portal backend's app settings are a declared Bicep array, so anything
 * written at deploy time is dropped the next time the Template Spec is redeployed. Stamping
 * it into the build instead means the reported version cannot drift from the code actually
 * running, and survives an infrastructure redeploy untouched.
 *
 * Any value that isn't a `mse-ci-v<major>.<minor>.<patch>` tag (such as this one) is treated
 * as unknown by the update check, which then reports no comparison rather than guessing.
 */
export const DEPLOYED_VERSION = 'dev';
