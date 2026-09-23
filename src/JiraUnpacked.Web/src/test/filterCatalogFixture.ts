import type { FilterCatalog } from '../api/issues';

/**
 * GET /api/filter-catalog as the server serves it after filter builder v2: every field's
 * operators in FilterCatalogService.DisplayOrder. Field order here is the tests' own (status
 * first); the server sorts fields by name, which nothing in the editor depends on.
 */
const DICT = ['Eq', 'Neq', 'In', 'NotIn', 'IsNull', 'IsNotNull'] as const;

export const FILTER_CATALOG: FilterCatalog = {
  fields: [
    { name: 'status', type: 'DictName', operators: [...DICT] },
    { name: 'issueType', type: 'DictName', operators: [...DICT] },
    { name: 'priority', type: 'DictName', operators: [...DICT] },
    { name: 'assignee', type: 'DictName', operators: [...DICT] },
    { name: 'sprint', type: 'DictName', operators: [...DICT] },
    { name: 'team', type: 'DictName', operators: [...DICT] },
    { name: 'storyPoints', type: 'Decimal', operators: ['Eq', 'Neq', 'Lt', 'Lte', 'Gt', 'Gte', 'IsNull', 'IsNotNull'] },
    { name: 'createdAt', type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
    { name: 'updatedAt', type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
    { name: 'summary', type: 'Text', operators: ['Contains', 'NotContains', 'StartsWith', 'IsNull', 'IsNotNull'] },
    { name: 'jiraKey', type: 'Text', operators: ['Eq', 'In', 'NotIn', 'Contains', 'StartsWith'] },
    { name: 'parent', type: 'ParentRef', operators: ['Eq', 'IsNull', 'IsNotNull'] },
  ],
  maxConditions: 20,
  maxInValues: 100,
  maxGroups: 10,
  maxDepth: 3,
};
