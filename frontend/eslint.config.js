import tseslint from "typescript-eslint";
import sonarjs from "eslint-plugin-sonarjs";

export default tseslint.config(
  tseslint.configs.recommended,
  sonarjs.configs.recommended,
);
